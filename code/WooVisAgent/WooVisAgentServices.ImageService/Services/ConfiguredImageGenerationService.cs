using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WooVisAgent.Services.Common.Contracts;
using WooVisAgent.Services.Common.Storage;
using WooVisAgent.SharedLib.Domain;
using WooVisAgent.SharedLib.Ids;
using WooVisAgentServices.ImageService.Contracts;
using WooVisAgentServices.ImageService.Serialization;
using WooVisAgentServices.ImageService.Support;
using WooVisAgentServices.ImageService.Validation;

namespace WooVisAgentServices.ImageService.Services;

/// <summary>
/// 图片生成服务实现。
///
/// 执行策略（两种，按模型配置自动选择）：
/// <list type="number">
///   <item>
///     模型配置了接口地址 → 调用远端模型接口（OpenAI Images 兼容协议），
///     支持返回 b64_json 或 url，拿到字节后统一落盘。
///   </item>
///   <item>
///     模型没填接口地址 → 用本地占位图渲染，仅用于打通"提交 → 队列 → 生成 → 回写状态 → 页面展示"链路。
///   </item>
/// </list>
/// 远端接口调用失败会直接抛异常，由 Worker 把任务置为失败态并带上可读原因，
/// 不会静默降级成占位图，避免"看起来成功了但其实是假图"。
/// </summary>
public sealed class ConfiguredImageGenerationService : IImageGenerationService
{
    /// <summary>从 url 拉取产物的体积上限，防止异常响应把磁盘写满</summary>
    private const long MaxDownloadBytes = 32L * 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly IFileStorageService _fileStorage;
    private readonly ISnowflakeIdGenerator _idGenerator;
    private readonly ILogger<ConfiguredImageGenerationService> _logger;

    public ConfiguredImageGenerationService(
        IHttpClientFactory httpClientFactory,
        IFileStorageService fileStorage,
        ISnowflakeIdGenerator idGenerator,
        ILogger<ConfiguredImageGenerationService> logger)
    {
        _httpClient = httpClientFactory.CreateClient(DependencyInjection.ImageGenerationHttpClientName);
        _fileStorage = fileStorage;
        _idGenerator = idGenerator;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TaskAsset>> GenerateAsync(
        ImageGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        var batchSize = Math.Clamp(request.BatchSize, 1, ImageGenerationInputValidator.MaxBatchSize);
        var useRemoteModel = !string.IsNullOrWhiteSpace(request.Model.Endpoint);

        if (!useRemoteModel)
        {
            _logger.LogWarning(
                "任务 {TaskId} 使用的模型 {ModelName} 未配置接口地址，改用本地占位图渲染",
                request.TaskId,
                request.Model.Name);
        }

        var assets = new List<TaskAsset>(batchSize);

        for (var index = 0; index < batchSize; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (bytes, extension) = useRemoteModel
                ? await RequestFromRemoteModelAsync(request, index, cancellationToken)
                : (PlaceholderImageRenderer.RenderPng(request.Prompt, request.Width, request.Height, index), "png");

            assets.Add(await SaveAssetAsync(request, bytes, extension, index, cancellationToken));
        }

        return assets;
    }

    private async Task<(byte[] Bytes, string Extension)> RequestFromRemoteModelAsync(
        ImageGenerationRequest request,
        int index,
        CancellationToken cancellationToken)
    {
        var url = BuildImagesUrl(request.Model.Endpoint!);

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

        // 指定了种子就按序号递增，保证一次生成多张时结果不同
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

        // 1) data 数组
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
        {
            var first = data[0];

            if (first.TryGetProperty("b64_json", out var base64) && base64.ValueKind == JsonValueKind.String)
                return (Convert.FromBase64String(base64.GetString()!), "png");

            if (first.TryGetProperty("url", out var remoteUrl) && remoteUrl.ValueKind == JsonValueKind.String)
                return await DownloadAsync(remoteUrl.GetString()!, cancellationToken);
        }

        // 2) images 数组（字符串，可能是 url 也可能是 base64）
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

    /// <summary>兼容 base 地址带不带 /v1、甚至直接写到 /images/generations 的情况</summary>
    private static string BuildImagesUrl(string endpoint)
    {
        var trimmed = endpoint.Trim().TrimEnd('/');

        if (trimmed.EndsWith("/images/generations", StringComparison.OrdinalIgnoreCase))
            return trimmed;

        if (trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            return $"{trimmed}/images/generations";

        return $"{trimmed}/v1/images/generations";
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
