using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WooVisAgent.Services.Common.Contracts;
using WooVisAgent.Services.Common.Storage;
using WooVisAgent.SharedLib.Delivery;
using WooVisAgent.SharedLib.Domain;
using WooVisAgent.SharedLib.Ids;
using WooVisAgentServices.ImageService.Contracts;
using WooVisAgentServices.ImageService.Serialization;
using WooVisAgentServices.ImageService.Support;
using WooVisAgentServices.ImageService.Validation;

namespace WooVisAgentServices.ImageService.Delivery;

/// <summary>
/// 基于「多协议解析」的模型交付框架实现。
///
/// 这是 IModelDeliveryFramework 的默认落地：
/// <list type="number">
///   <item>先通过 <see cref="ModelDeliveryProtocolResolver"/> 把模型配置解析成真正要请求的 URL（修复 404）；</item>
///   <item>没有匹配协议、或没填接口地址时，走本地占位图渲染，保证链路可验证；</item>
///   <item>远端调用返回 b64_json / url / images 三种形态都能解析并落盘；</item>
///   <item>连通性测试复用同一套协议解析，返回解析后的 URL 供排查。</item>
/// </list>
/// 未来接入 Microsoft.Agents.AI 的 AIAgent 时，可以新增一个实现类替换本类，
/// 上层 Worker / 用例无需任何改动。
/// </summary>
public sealed class ModelDeliveryFramework : IModelDeliveryFramework
{
    /// <summary>从 url 拉取产物的体积上限，防止异常响应把磁盘写满</summary>
    private const long MaxDownloadBytes = 32L * 1024 * 1024;

    /// <summary>连通性探测超时</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);

    private readonly HttpClient _httpClient;
    private readonly IFileStorageService _fileStorage;
    private readonly ISnowflakeIdGenerator _idGenerator;
    private readonly ModelDeliveryProtocolResolver _protocolResolver;
    private readonly ILogger<ModelDeliveryFramework> _logger;

    public ModelDeliveryFramework(
        IHttpClientFactory httpClientFactory,
        IFileStorageService fileStorage,
        ISnowflakeIdGenerator idGenerator,
        ModelDeliveryProtocolResolver protocolResolver,
        ILogger<ModelDeliveryFramework> logger)
    {
        _httpClient = httpClientFactory.CreateClient(DependencyInjection.ImageGenerationHttpClientName);
        _fileStorage = fileStorage;
        _idGenerator = idGenerator;
        _protocolResolver = protocolResolver;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TaskAsset>> GenerateImageAsync(
        ImageGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        var batchSize = Math.Clamp(request.BatchSize, 1, ImageGenerationInputValidator.MaxBatchSize);
        var url = _protocolResolver.ResolveRequestUrl(request.Model);
        var useRemoteModel = !string.IsNullOrWhiteSpace(url);

        if (!useRemoteModel)
        {
            _logger.LogWarning(
                "任务 {TaskId} 使用的模型 {ModelName} 未配置可解析的接口地址，改用本地占位图渲染",
                request.TaskId,
                request.Model.Name);
        }

        var assets = new List<TaskAsset>(batchSize);

        for (var index = 0; index < batchSize; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (bytes, extension) = useRemoteModel
                ? await RequestFromRemoteModelAsync(request, url!, index, cancellationToken)
                : (PlaceholderImageRenderer.RenderPng(request.Prompt, request.Width, request.Height, index), "png");

            assets.Add(await SaveAssetAsync(request, bytes, extension, index, cancellationToken));
        }

        return assets;
    }

    public async Task<ModelDeliveryProbeResult> TestConnectivityAsync(
        ModelConfig model,
        CancellationToken cancellationToken = default)
    {
        var label = string.IsNullOrWhiteSpace(model.Name) ? "该模型" : $"「{model.Name.Trim()}」";

        if (string.IsNullOrWhiteSpace(model.Endpoint))
            return new ModelDeliveryProbeResult(false, null, $"{label}尚未配置接口地址，无法连通。", null);

        var url = _protocolResolver.ResolveRequestUrl(model);
        if (string.IsNullOrWhiteSpace(url))
        {
            return new ModelDeliveryProbeResult(
                false,
                null,
                $"{label}的接口地址无法被识别为任何已知协议，请填写完整生成路径。",
                null);
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            if (!string.IsNullOrWhiteSpace(model.ApiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", model.ApiKey.Trim());

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);

            stopwatch.Stop();

            // 404/405 说明地址可达但方法/路径可能不对，也算"能连上"，附带明确提示
            var reachable = response.StatusCode is
                System.Net.HttpStatusCode.OK
                or System.Net.HttpStatusCode.NoContent
                or System.Net.HttpStatusCode.NotFound
                or System.Net.HttpStatusCode.MethodNotAllowed
                or System.Net.HttpStatusCode.Unauthorized;

            return new ModelDeliveryProbeResult(
                reachable,
                (int)stopwatch.ElapsedMilliseconds,
                reachable
                    ? $"地址可达（HTTP {(int)response.StatusCode}），解析后的请求地址为 {url}"
                    : $"地址返回 HTTP {(int)response.StatusCode}，请检查接口配置。",
                url);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new ModelDeliveryProbeResult(
                false,
                (int)stopwatch.ElapsedMilliseconds,
                $"探测超时（{ProbeTimeout.TotalSeconds:0} 秒），请检查接口地址与网络。",
                url);
        }
        catch (HttpRequestException exception)
        {
            stopwatch.Stop();
            return new ModelDeliveryProbeResult(
                false,
                (int)stopwatch.ElapsedMilliseconds,
                $"连接失败：{exception.Message}",
                url);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            stopwatch.Stop();
            return new ModelDeliveryProbeResult(false, null, $"测试异常：{exception.Message}", url);
        }
    }

    private async Task<(byte[] Bytes, string Extension)> RequestFromRemoteModelAsync(
        ImageGenerationRequest request,
        string url,
        int index,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = string.IsNullOrWhiteSpace(request.Model.ModelKey) ? "image-1" : request.Model.ModelKey,
            ["prompt"] = request.Prompt,
            ["n"] = 1,
            ["size"] = $"{request.Width}x{request.Height}",
            ["response_format"] = "b64_json",
            ["steps"] = request.Steps,
            ["cfg_scale"] = request.CfgScale,
        };

        if (!string.IsNullOrWhiteSpace(request.NegativePrompt))
            payload["negative_prompt"] = request.NegativePrompt;

        if (request.Seed is { } seed)
            payload["seed"] = seed + index;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, GenerationTaskSerialization.Options),
                Encoding.UTF8,
                "application/json"),
        };

        if (!string.IsNullOrWhiteSpace(request.Model.ApiKey))
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.Model.ApiKey.Trim());

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"模型接口返回 {(int)response.StatusCode}：{Truncate(body, 300)}");

        return await ExtractImageAsync(response, body, cancellationToken);
    }

    /// <summary>
    /// 兼容三种常见返回：
    /// <c>data[].b64_json</c>（OpenAI）、<c>data[].url</c>、<c>images[]</c>（自建推理网关）。
    /// </summary>
    private async Task<(byte[] Bytes, string Extension)> ExtractImageAsync(
        HttpResponseMessage response,
        string body,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
        {
            var first = data[0];

            if (first.TryGetProperty("b64_json", out var base64) && base64.ValueKind == JsonValueKind.String)
                return (Convert.FromBase64String(base64.GetString()!), "png");

            if (first.TryGetProperty("url", out var remoteUrl) && remoteUrl.ValueKind == JsonValueKind.String)
                return await DownloadAsync(remoteUrl.GetString()!, cancellationToken);
        }

        if (root.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array && images.GetArrayLength() > 0)
        {
            var first = images[0].GetString() ?? string.Empty;
            if (first.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return await DownloadAsync(first, cancellationToken);

            return (Convert.FromBase64String(first), GuessExtension(response.Content.Headers.ContentType?.MediaType));
        }

        throw new InvalidOperationException($"模型接口未返回可识别的图片数据：{Truncate(body, 300)}");
    }

    private async Task<(byte[] Bytes, string Extension)> DownloadAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is { } length && length > MaxDownloadBytes)
            throw new InvalidOperationException("模型返回的图片体积过大，已终止下载。");

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length > MaxDownloadBytes)
            throw new InvalidOperationException("模型返回的图片体积过大，已终止下载。");

        return (bytes, GuessExtension(response.Content.Headers.ContentType?.MediaType));
    }

    private async Task<TaskAsset> SaveAssetAsync(
        ImageGenerationRequest request,
        byte[] bytes,
        string extension,
        int index,
        CancellationToken cancellationToken)
    {
        var fileName = $"task-{request.TaskId}-{index + 1}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.{extension}";

        await using var stream = new MemoryStream(bytes, writable: false);
        var relativePath = await _fileStorage.SaveAsync(stream, fileName, cancellationToken);

        return new TaskAsset
        {
            Id = _idGenerator.NextId().ToString(CultureInfo.InvariantCulture),
            Type = "image",
            Url = BuildPublicUrl(relativePath),
            FileName = fileName,
            FileSize = bytes.Length,
            Width = request.Width,
            Height = request.Height,
        };
    }

    /// <summary>
    /// 把存储层返回的相对路径（形如 <c>uploads/2026/10/16/xxx.png</c>）
    /// 转成页面可直接使用的访问地址（<c>/uploads/2026/10/16/xxx.png</c>）。
    /// </summary>
    private static string BuildPublicUrl(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        var uploadPrefix = $"{LocalStorageDefaults.UploadFolder}/";

        if (normalized.StartsWith(uploadPrefix, StringComparison.OrdinalIgnoreCase))
            normalized = normalized[uploadPrefix.Length..];

        return $"{LocalStorageDefaults.PublicRequestPath}/{normalized}";
    }

    private static string GuessExtension(string? mediaType) => mediaType?.ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" => "jpg",
        "image/webp" => "webp",
        _ => "png",
    };

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
