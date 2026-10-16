using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Polly.Timeout;
using WooVisAgent.Services.Common.Contracts;
using WooVisAgent.Services.Common.Storage;
using WooVisAgent.SharedLib.Delivery;
using WooVisAgent.SharedLib.Domain;
using WooVisAgent.SharedLib.Ids;
using WooVisAgentServices.ImageService.Contracts;
using WooVisAgentServices.ImageService.Support;
using WooVisAgentServices.ImageService.Validation;

namespace WooVisAgentServices.ImageService.Delivery;

/// <summary>
/// 基于「多协议解析」的模型交付框架实现。
///
/// 职责边界：
/// <list type="number">
///   <item>通过 <see cref="ModelDeliveryProtocolResolver"/> 解析出「协议 + 请求地址 + 请求体」（修复 404）；</item>
///   <item>按协议决定是<b>同步取图</b>（OpenAI Images 系）还是<b>提交任务后轮询</b>（DashScope 原生异步）；</item>
///   <item>把 b64_json / url / 各家 results 结构统一解析成字节流并落盘；</item>
///   <item>没填接口地址时走本地占位图，保证链路可端到端验证；</item>
///   <item>连通性测试复用同一套协议解析，并把解析后的 URL 回传，便于排查 404。</item>
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

    /// <summary>异步任务轮询间隔（阿里云官方建议 3-5 秒）</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    /// <summary>异步任务整体等待上限</summary>
    private static readonly TimeSpan PollTimeout = TimeSpan.FromMinutes(3);

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

        // 解析时同时拿到命中的协议，后续日志与错误信息都要带上，方便排查 404 / 协议不符这类问题
        var resolution = _protocolResolver.Resolve(request.Model);
        var protocol = resolution?.Protocol;

        if (protocol is not null)
        {
            _logger.LogInformation(
                "任务 {TaskId} 使用模型 {ModelName}（供应方 {Provider}）：协议 {Protocol}（{Mode}）解析出请求地址 {Url}",
                request.TaskId,
                request.Model.Name,
                request.Model.Provider,
                protocol.Name,
                protocol.IsAsynchronous ? "异步提交+轮询" : "同步",
                resolution!.Url);
        }
        else
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

            (byte[] Bytes, string Extension) produced;

            try
            {
                produced = protocol is not null
                    ? await RequestFromRemoteModelAsync(request, resolution!, index, cancellationToken)
                    : (PlaceholderImageRenderer.RenderPng(request.Prompt, request.Width, request.Height, index), "png");
            }
            catch (TimeoutRejectedException exception)
            {
                // 弹性管道（Polly）判定超时。原始信息是一句英文的
                // “The operation didn't complete within the allowed timeout of '00:04:00'.”，
                // 直接落到任务列表里没人看得懂，这里翻译成可执行的提示。
                throw new InvalidOperationException(
                    $"模型调用超时：等待已超过弹性管道允许的上限仍未返回（请求地址：{resolution?.Url}，协议：{protocol?.Name}）。" +
                    "图片生成属于典型的慢调用，请先确认模型服务可达、模型名有效；" +
                    "若模型本身就慢，请调大 ServiceDefaults 项目里 HttpResilienceDefaults 的超时配置。" +
                    $"原始错误：{exception.Message}",
                    exception);
            }

            assets.Add(await SaveAssetAsync(request, produced.Bytes, produced.Extension, index, cancellationToken));
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

        var resolution = _protocolResolver.Resolve(model);
        if (resolution is null)
        {
            return new ModelDeliveryProbeResult(
                false,
                null,
                $"{label}的接口地址无法被识别为任何已知协议，请填写完整生成路径。",
                null);
        }

        var url = resolution.Url;
        var protocolName = resolution.Protocol.Name;
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
                    ? $"地址可达（HTTP {(int)response.StatusCode}），协议 {protocolName}，解析后的请求地址为 {url}"
                    : $"地址返回 HTTP {(int)response.StatusCode}（协议 {protocolName}，请求地址 {url}），请检查接口配置。",
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
        catch (TimeoutRejectedException exception)
        {
            stopwatch.Stop();
            return new ModelDeliveryProbeResult(
                false,
                (int)stopwatch.ElapsedMilliseconds,
                $"探测被弹性管道超时策略中断，请检查接口地址与网络。原始错误：{exception.Message}",
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

    /// <summary>
    /// 把一次生成请求投递到模型：按「方案列表」依次尝试，直到某一套成功。
    ///
    /// 同一家云常常有不止一套入口（例如百炼的 兼容入口 / 原生同步 / 原生异步），
    /// 用户填的地址 + 模型名未必指向正确的那一套。把候选方案串成链条后，
    /// "地址填成兼容入口"、"模型系列猜错" 这类问题会自动纠正，
    /// 而不是像以前那样直接变成一条任务失败记录。
    /// </summary>
    private async Task<(byte[] Bytes, string Extension)> RequestFromRemoteModelAsync(
        ImageGenerationRequest request,
        ProtocolResolution resolution,
        int index,
        CancellationToken cancellationToken)
    {
        var deliveryRequest = new ImageDeliveryRequest(
            request.Model.ModelKey?.Trim(),
            request.Prompt,
            request.NegativePrompt,
            request.Width,
            request.Height,
            request.Steps,
            request.CfgScale,
            request.Seed,
            index);

        var plans = BuildPlans(request.Model, resolution);
        var failures = new List<string>(plans.Count);

        for (var position = 0; position < plans.Count; position++)
        {
            var (protocol, url) = plans[position];
            var outcome = await TryDeliverAsync(request, url, protocol, deliveryRequest, cancellationToken);

            if (outcome.Result is { } succeeded)
                return succeeded;

            failures.Add(outcome.Error!);

            if (!outcome.CanFallback)
                break;

            _logger.LogWarning(
                "任务 {TaskId} 的第 {Position}/{Total} 套投递方案不可用（协议 {Protocol}，地址 {Url}），自动切换下一套：{Error}",
                request.TaskId,
                position + 1,
                plans.Count,
                protocol.Name,
                url,
                outcome.Error);
        }

        throw new InvalidOperationException(DescribeFailures(failures));
    }

    /// <summary>
    /// 组装候选投递方案：首选（解析所得）+ 协议声明的兜底方案，并按地址去重。
    /// </summary>
    private static List<(IModelDeliveryProtocol Protocol, string Url)> BuildPlans(
        ModelConfig model,
        ProtocolResolution resolution)
    {
        var plans = new List<(IModelDeliveryProtocol, string)> { (resolution.Protocol, resolution.Url) };

        foreach (var fallback in resolution.Protocol.GetFallbackProtocols(model))
        {
            var url = fallback.ResolveRequestUrl(model);
            if (string.IsNullOrWhiteSpace(url))
                continue;

            if (plans.Any(existing => string.Equals(existing.Item2, url, StringComparison.OrdinalIgnoreCase)))
                continue;

            plans.Add((fallback, url));
        }

        return plans;
    }

    /// <summary>执行一套方案：必要时做「可选字段被拒 → 极简体重试」，最后归一化成成败结论</summary>
    private async Task<DeliveryOutcome> TryDeliverAsync(
        ImageGenerationRequest request,
        string url,
        IModelDeliveryProtocol protocol,
        ImageDeliveryRequest deliveryRequest,
        CancellationToken cancellationToken)
    {
        var body = protocol.BuildRequestBody(deliveryRequest);
        var attempt = await PostAsync(request, url, protocol, deliveryRequest, body, cancellationToken);

        // 异构网关对可选字段的容忍度差异很大（例如百炼的 negative_prompt 仅部分模型支持）。
        // 首个请求被 400 拒绝时，用只含必需字段的极简请求体自动降级重试一次，
        // 避免"少一个字段就整条任务失败"。400 表示请求在处理前就被拒，不会重复计费。
        if (attempt.Response.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            var minimalBody = protocol.BuildMinimalRequestBody(deliveryRequest);
            if (!string.Equals(minimalBody, body, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "模型接口返回 400（请求地址 {Url}，协议 {Protocol}），疑似网关不接受可选字段，已改用极简请求体重试。原始响应：{Body}",
                    url,
                    protocol.Name,
                    Truncate(attempt.Body, 300));

                attempt.Response.Dispose();
                attempt = await PostAsync(request, url, protocol, deliveryRequest, minimalBody, cancellationToken);
            }
        }

        using var response = attempt.Response;
        var responseBody = attempt.Body;

        if (!response.IsSuccessStatusCode)
        {
            // 错误信息带上"实际请求的地址 + 命中的协议"，这样 404 之类的地址类问题一眼可查，
            // 不用再去翻日志定位到底拼成了什么 URL。
            _logger.LogError(
                "模型接口返回 {StatusCode}。请求地址={Url}，协议={Protocol}，供应方={Provider}，响应={Body}",
                (int)response.StatusCode,
                url,
                protocol.Name,
                request.Model.Provider,
                Truncate(responseBody, 500));

            var error = $"模型接口返回 {(int)response.StatusCode}（请求地址：{url}，协议：{protocol.Name}）：{Truncate(responseBody, 300)}";

            // 只有"路径不存在"与"模型不属于这套接口"才值得换方案；
            // 鉴权失败、限流、内容审核不通过等换方案也没用，直接如实报错。
            var canFallback = response.StatusCode == System.Net.HttpStatusCode.NotFound
                              || LooksLikeModelMissing(responseBody);

            return DeliveryOutcome.Failed(error, canFallback);
        }

        // 同步协议：响应体里直接就是图片
        if (!protocol.IsAsynchronous)
            return DeliveryOutcome.Success(
                await ExtractImageAsync(responseBody, response.Content.Headers.ContentType?.MediaType, cancellationToken));

        return DeliveryOutcome.Success(
            await AwaitAsyncTaskResultAsync(request, url, protocol, responseBody, cancellationToken));
    }

    /// <summary>
    /// 异步协议：先从提交响应里取 task_id，再轮询任务结果，最后把结果响应体解析成图片。
    /// </summary>
    private async Task<(byte[] Bytes, string Extension)> AwaitAsyncTaskResultAsync(
        ImageGenerationRequest request,
        string submitUrl,
        IModelDeliveryProtocol protocol,
        string submitBody,
        CancellationToken cancellationToken)
    {
        var modelTaskId = ExtractTaskId(submitBody);
        if (string.IsNullOrWhiteSpace(modelTaskId))
            throw new InvalidOperationException(
                $"协议 {protocol.Name} 为异步调用，但提交响应里没有 task_id（请求地址：{submitUrl}）：{Truncate(submitBody, 300)}");

        var statusUrl = protocol.ResolveTaskStatusUrl(request.Model, modelTaskId)
            ?? throw new InvalidOperationException(
                $"协议 {protocol.Name} 为异步调用，但无法构造任务查询地址（请求地址：{submitUrl}）。");

        _logger.LogInformation(
            "任务 {TaskId} 已提交到模型，模型任务号 {ModelTaskId}，轮询地址 {StatusUrl}",
            request.TaskId,
            modelTaskId,
            statusUrl);

        var resultBody = await PollTaskUntilCompletedAsync(request, protocol, statusUrl, cancellationToken);
        return await ExtractImageAsync(resultBody, null, cancellationToken);
    }

    /// <summary>
    /// 响应体是否在说「这个模型不属于这套接口」。
    /// 用来区分"地址对了但模型系列不对"和别的 400 —— 前者应该换方案，
    /// 后者（参数非法、内容审核等）换方案没有意义。
    /// </summary>
    private static bool LooksLikeModelMissing(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return false;

        return body.Contains("Model not exist", StringComparison.OrdinalIgnoreCase)
               || body.Contains("model_not_found", StringComparison.OrdinalIgnoreCase)
               || body.Contains("ModelNotFound", StringComparison.OrdinalIgnoreCase)
               || body.Contains("UnsupportedModel", StringComparison.OrdinalIgnoreCase)
               || body.Contains("InvalidParameter.Model", StringComparison.OrdinalIgnoreCase)
               || body.Contains("does not exist", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 汇总所有失败方案的错误。多方案时保留每一套「状态码 + 地址 + 协议」，
    /// 这样用户在任务列表里就能看出框架到底试了哪些地址，而不用去翻日志。
    /// </summary>
    private static string DescribeFailures(IReadOnlyList<string> failures)
    {
        if (failures.Count == 0)
            return "模型调用失败：没有可用的投递方案。";

        if (failures.Count == 1)
            return failures[0];

        return Truncate($"已依次尝试 {failures.Count} 套投递方案，全部失败 —— {string.Join(" ｜ ", failures)}", 900);
    }

    /// <summary>一套投递方案的执行结论</summary>
    private readonly record struct DeliveryOutcome(
        (byte[] Bytes, string Extension)? Result,
        string? Error,
        bool CanFallback)
    {
        public static DeliveryOutcome Success((byte[] Bytes, string Extension) result) => new(result, null, false);

        public static DeliveryOutcome Failed(string error, bool canFallback) => new(null, error, canFallback);
    }

    /// <summary>按协议构造并发送一次生成请求，返回响应（调用方负责释放）与响应体</summary>
    private async Task<(HttpResponseMessage Response, string Body)> PostAsync(
        ImageGenerationRequest request,
        string url,
        IModelDeliveryProtocol protocol,
        ImageDeliveryRequest deliveryRequest,
        string requestBody,
        CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json"),
        };

        if (!string.IsNullOrWhiteSpace(request.Model.ApiKey))
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.Model.ApiKey.Trim());

        foreach (var (key, value) in protocol.BuildExtraHeaders(deliveryRequest))
            httpRequest.Headers.TryAddWithoutValidation(key, value);

        var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return (response, body);
    }

    /// <summary>
    /// 轮询任务结果，直到 SUCCEEDED 或失败/超时。
    /// 返回的是任务查询接口的最终响应体，交给 <see cref="ExtractImageAsync"/> 统一解析。
    /// </summary>
    private async Task<string> PollTaskUntilCompletedAsync(
        ImageGenerationRequest request,
        IModelDeliveryProtocol protocol,
        string statusUrl,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PollTimeout);

        try
        {
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();

                using var probe = new HttpRequestMessage(HttpMethod.Get, statusUrl);
                if (!string.IsNullOrWhiteSpace(request.Model.ApiKey))
                    probe.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.Model.ApiKey.Trim());

                using var response = await _httpClient.SendAsync(probe, timeout.Token);
                var body = await response.Content.ReadAsStringAsync(timeout.Token);

                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException(
                        $"查询模型任务状态失败：HTTP {(int)response.StatusCode}（请求地址：{statusUrl}，协议：{protocol.Name}）：{Truncate(body, 300)}");

                var status = ReadTaskStatus(body);

                if (IsSucceededStatus(status))
                    return body;

                if (IsFailedStatus(status))
                    throw new InvalidOperationException(
                        $"模型任务执行失败（状态 {status}，请求地址：{statusUrl}，协议：{protocol.Name}）：{Truncate(body, 300)}");

                await Task.Delay(PollInterval, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"等待模型任务完成超时（{PollTimeout.TotalMinutes:0} 分钟，请求地址：{statusUrl}，协议：{protocol.Name}），请稍后重试。");
        }
    }

    /// <summary>从提交响应里取 task_id，兼容 output.task_id 与根级 task_id / taskId 三种写法</summary>
    private static string? ExtractTaskId(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.TryGetProperty("output", out var output)
                && output.ValueKind == JsonValueKind.Object
                && output.TryGetProperty("task_id", out var nested)
                && nested.ValueKind == JsonValueKind.String)
                return nested.GetString();

            foreach (var name in new[] { "task_id", "taskId" })
            {
                if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                    return value.GetString();
            }
        }
        catch (JsonException)
        {
            // 响应不是合法 JSON，交给上层报"没有 task_id"，错误信息里会带上原始响应
        }

        return null;
    }

    /// <summary>读取任务状态，兼容 output.task_status 与根级 status</summary>
    private static string? ReadTaskStatus(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.TryGetProperty("output", out var output)
                && output.ValueKind == JsonValueKind.Object
                && output.TryGetProperty("task_status", out var nested)
                && nested.ValueKind == JsonValueKind.String)
                return nested.GetString()?.Trim().ToUpperInvariant();

            foreach (var name in new[] { "task_status", "status" })
            {
                if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                    return value.GetString()?.Trim().ToUpperInvariant();
            }
        }
        catch (JsonException)
        {
            // 交给调用方按"未知状态"处理（继续轮询直到超时）
        }

        return null;
    }

    private static bool IsSucceededStatus(string? status)
        => status is "SUCCEEDED" or "SUCCESS" or "SUCCEED" or "COMPLETED" or "COMPLETE";

    private static bool IsFailedStatus(string? status)
        => status is "FAILED" or "FAIL" or "CANCELED" or "CANCELLED" or "UNKNOWN" or "ERROR";

    /// <summary>
    /// 把各家的响应统一解析成图片字节流，覆盖四类常见结构：
    /// <list type="bullet">
    ///   <item><c>data[].b64_json</c> / <c>data[].url</c>（OpenAI 及兼容网关、百炼兼容模式）</item>
    ///   <item><c>images[]</c>（自建推理网关，元素为 URL 或 base64）</item>
    ///   <item><c>output.results[].url</c>（DashScope 原生异步任务结果）</item>
    ///   <item><c>output.choices[].message.content[].image</c>（百炼多模态图像生成结果）</item>
    /// </list>
    /// </summary>
    private async Task<(byte[] Bytes, string Extension)> ExtractImageAsync(
        string body,
        string? mediaType,
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

        if (root.TryGetProperty("images", out var images)
            && images.ValueKind == JsonValueKind.Array
            && images.GetArrayLength() > 0)
        {
            var first = images[0].GetString() ?? string.Empty;
            if (first.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return await DownloadAsync(first, cancellationToken);

            return (Convert.FromBase64String(first), GuessExtension(mediaType));
        }

        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Object)
        {
            // DashScope 原生异步：output.results[].url
            if (output.TryGetProperty("results", out var results)
                && results.ValueKind == JsonValueKind.Array
                && results.GetArrayLength() > 0)
            {
                var first = results[0];

                if (first.TryGetProperty("url", out var resultUrl) && resultUrl.ValueKind == JsonValueKind.String)
                    return await DownloadAsync(resultUrl.GetString()!, cancellationToken);

                if (first.TryGetProperty("b64_image", out var resultBase64) && resultBase64.ValueKind == JsonValueKind.String)
                    return (Convert.FromBase64String(resultBase64.GetString()!), "png");
            }

            // 百炼多模态图像生成：output.choices[].message.content[].image
            if (output.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("message", out var message)
                && message.TryGetProperty("content", out var contents)
                && contents.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in contents.EnumerateArray())
                {
                    if (item.TryGetProperty("image", out var imageUrl)
                        && imageUrl.ValueKind == JsonValueKind.String
                        && imageUrl.GetString() is { Length: > 0 } url)
                        return await DownloadAsync(url, cancellationToken);
                }
            }

            // 任务失败时把原因带出来，比"未返回可识别的图片数据"更有用
            if (output.TryGetProperty("message", out var failureMessage)
                && failureMessage.ValueKind == JsonValueKind.String)
            {
                throw new InvalidOperationException(
                    $"模型任务未产出图片：{failureMessage.GetString()}（原始响应：{Truncate(body, 300)}）");
            }
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
