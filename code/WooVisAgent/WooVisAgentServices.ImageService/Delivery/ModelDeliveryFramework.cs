using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Sockets;
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

        // 进度上报（可选）。没有传通道时全部是空操作，行为与以前完全一致。
        var progress = request.Progress is { } reporter
            ? new ProgressScope(reporter, 0, 100)
            : null;

        progress?.ReportRatio(0.06, "正在解析模型接口");

        // 参考图必须先解析成字节：它们存在本站 /uploads 下，模型服务访问不到这个地址，
        // 只能由本进程读出来再按协议需要转成 multipart 文件字段或 data URI。
        var references = await ResolveReferenceImagesAsync(request, cancellationToken);
        var hasReferences = references.Count > 0;

        // 图片翻译接口要求参考图是「公网可访问的 http(s) 地址」，
        // 本站 /uploads 下的文件模型服务取不到，必须在发请求前就说清楚，
        // 否则百炼只会回一句含糊的 url error。
        if (references.Count > 0 && AliyunDashScopeEndpoints.IsImageTranslateModel(request.Model.ModelKey))
        {
            var reference = references[0];

            if (!Uri.TryCreate(reference.Url, UriKind.Absolute, out var referenceUri)
                || referenceUri.Scheme is not ("http" or "https"))
            {
                throw new InvalidOperationException(
                    $"模型「{request.Model.Name}」要求参考图必须是<b>公网可访问的 http(s) 地址</b>，当前参考图是本站相对地址" +
                    $"（{reference.Url}），百炼服务访问不到。请改用可直接访问的图片链接作为参考图。" +
                    "若你的目的是生成图片而非翻译图片，请改用文生图模型，例如 qwen-image-2.1-pro。");
            }
        }

        // 解析时同时拿到命中的协议，后续日志与错误信息都要带上，方便排查 404 / 协议不符这类问题。
        // 是否带参考图会影响入口路径（OpenAI 系图生图走 /images/edits），必须在这一步就告知解析器。
        var resolution = _protocolResolver.Resolve(request.Model, hasReferences);
        var protocol = resolution?.Protocol;

        if (protocol is not null)
        {
            _logger.LogInformation(
                "任务 {TaskId} 使用模型 {ModelName}（供应方 {Provider}）：协议 {Protocol}（{Mode}）解析出请求地址 {Url}，模式 {TaskMode}",
                request.TaskId,
                request.Model.Name,
                request.Model.Provider,
                protocol.Name,
                protocol.IsAsynchronous ? "异步提交+轮询" : "同步",
                resolution!.Url,
                hasReferences ? $"图生图（{references.Count} 张参考图）" : "文生图");

            // 协议不支持带图时直接失败。若在这里把参考图悄悄丢掉、照常文生图，
            // 产出的是一张与参考图毫无关系的图，用户无法判断是模型不行还是没接上。
            if (hasReferences && !protocol.SupportsImageToImage)
            {
                throw new InvalidOperationException(
                    $"模型「{request.Model.Name}」命中的协议 {protocol.Name} 不支持图生图（参考图）。" +
                    "请改用支持图生图的模型（如 OpenAI 系 /images/edits、百炼 qwen-image 多模态入口），" +
                    "或去掉参考图改为纯文生图。");
            }

            // 图片翻译模型（qwen-mt-image）没有纯文生图入口：它只能拿一张已有的图，
            // 把图里的文字翻译成另一种语言。用提示词去调它，百炼会回一句
            // "url error, please check url"，真实原因（模型与入口不匹配）完全看不出来。
            // 所以在发请求前拦下来，把真实原因讲清楚。
            if (!hasReferences && !protocol.SupportsTextToImage)
            {
                throw new InvalidOperationException(
                    $"模型「{request.Model.Name}」（{request.Model.ModelKey}）是<b>图片翻译模型</b>，不是文生图模型。" +
                    "它的输入模态只有「图片 → 图片」：必须上传一张待翻译的原图，再指定源语言与目标语言，" +
                    "请求体里没有 prompt 字段，因此无法用一句提示词从零生成图片。" +
                    "若你的目的是生成图片，请改用文生图模型，例如百炼的 " +
                    "qwen-image-2.1-pro / qwen-image-2.0-pro / wan2.6-t2i。" +
                    "若确实要做图片翻译，请先在对话中上传一张图片作为参考图。");
            }
        }
        else
        {
            _logger.LogWarning(
                "任务 {TaskId} 使用的模型 {ModelName} 未配置可解析的接口地址，改用本地占位图渲染",
                request.TaskId,
                request.Model.Name);
        }

        progress?.ReportRatio(0.12, hasReferences ? $"已读取 {references.Count} 张参考图" : "正在提交生成请求");

        var assets = new List<TaskAsset>(batchSize);

        // 单张图的进度区间：整体 15% → 92%，多张时按张数均分，
        // 这样"出第 2/4 张"这种中间状态也能真实反映到进度条上。
        const double imageRangeStart = 0.15;
        const double imageRangeEnd = 0.92;
        var slice = (imageRangeEnd - imageRangeStart) / batchSize;

        for (var index = 0; index < batchSize; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var scope = progress?.Slice(
                imageRangeStart + slice * index,
                imageRangeStart + slice * (index + 1));

            scope?.ReportRatio(
                0.1,
                batchSize > 1 ? $"正在生成第 {index + 1}/{batchSize} 张" : "正在调用模型生成");

            (byte[] Bytes, string Extension) produced;

            try
            {
                produced = protocol is not null
                    ? await RequestFromRemoteModelAsync(request, resolution!, index, references, scope, cancellationToken)
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

            scope?.ReportRatio(0.93, "正在保存图片");
            assets.Add(await SaveAssetAsync(request, produced.Bytes, produced.Extension, index, cancellationToken));
            scope?.ReportRatio(1, null);
        }

        progress?.ReportRatio(0.96, "正在写入任务结果");
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
        IReadOnlyList<ImageDeliveryReference> references,
        ProgressScope? progress,
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
            index,
            references);

        var plans = BuildPlans(request.Model, resolution, references.Count > 0);
        var failures = new List<string>(plans.Count);

        progress?.ReportRatio(0.2, "正在提交生成请求");

        for (var position = 0; position < plans.Count; position++)
        {
            var (protocol, url) = plans[position];
            var outcome = await TryDeliverAsync(request, url, protocol, deliveryRequest, progress, cancellationToken);

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
        ProtocolResolution resolution,
        bool hasReferenceImages)
    {
        var plans = new List<(IModelDeliveryProtocol, string)> { (resolution.Protocol, resolution.Url) };

        foreach (var fallback in resolution.Protocol.GetFallbackProtocols(model))
        {
            var url = fallback.ResolveRequestUrl(model, hasReferenceImages);
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
        ProgressScope? progress,
        CancellationToken cancellationToken)
    {
        // 图生图在 OpenAI 系必须走 multipart 文件字段，其余仍是 JSON
        var multipart = protocol.RequiresMultipart(deliveryRequest);

        var attempt = await SendAsync(request, url, protocol, deliveryRequest, multipart, false, cancellationToken);

        // 异构网关对可选字段的容忍度差异很大（例如百炼的 negative_prompt 仅部分模型支持）。
        // 首个请求被 400 拒绝时，用只含必需字段的极简请求体自动降级重试一次，
        // 避免"少一个字段就整条任务失败"。400 表示请求在处理前就被拒，不会重复计费。
        if (attempt.Response.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            _logger.LogWarning(
                "模型接口返回 400（请求地址 {Url}，协议 {Protocol}，{ContentType}），疑似网关不接受可选字段，已改用极简请求重试。原始响应：{Body}",
                url,
                protocol.Name,
                multipart ? "multipart" : "json",
                Truncate(attempt.Body, 300));

            attempt.Response.Dispose();
            attempt = await SendAsync(request, url, protocol, deliveryRequest, multipart, true, cancellationToken);
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

            var error = ExplainFailure((int)response.StatusCode, responseBody, url, protocol.Name);

            // 只有"路径不存在"与"模型不属于这套接口"才值得换方案；
            // 鉴权失败、限流、内容审核不通过等换方案也没用，直接如实报错。
            var canFallback = response.StatusCode == System.Net.HttpStatusCode.NotFound
                              || LooksLikeModelMissing(responseBody);

            return DeliveryOutcome.Failed(error, canFallback);
        }

        // 同步协议：响应体里直接就是图片
        if (!protocol.IsAsynchronous)
        {
            progress?.ReportRatio(0.75, "模型已返回，正在读取图片");

            return DeliveryOutcome.Success(
                await ExtractImageAsync(responseBody, response.Content.Headers.ContentType?.MediaType, cancellationToken));
        }

        return DeliveryOutcome.Success(
            await AwaitAsyncTaskResultAsync(request, url, protocol, responseBody, progress, cancellationToken));
    }

    /// <summary>
    /// 异步协议：先从提交响应里取 task_id，再轮询任务结果，最后把结果响应体解析成图片。
    /// </summary>
    private async Task<(byte[] Bytes, string Extension)> AwaitAsyncTaskResultAsync(
        ImageGenerationRequest request,
        string submitUrl,
        IModelDeliveryProtocol protocol,
        string submitBody,
        ProgressScope? progress,
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

        var resultBody = await PollTaskUntilCompletedAsync(request, protocol, statusUrl, progress, cancellationToken);
        return await ExtractImageAsync(resultBody, null, cancellationToken);
    }

    /// <summary>
    /// 把云端返回的错误翻译成"能据此采取行动"的中文说明。
    ///
    /// 百炼有几类错误的英文原文完全看不出该做什么：
    /// <list type="bullet">
    ///   <item><c>AllocationQuota.FreeTierOnly</c> —— 免费额度用尽且账号开启了"仅用免费额度"。
    ///       英文里只说 "add funds"，用户根本不知道去哪个控制台开关；</item>
    ///   <item><c>Throttling</c> —— 触发限流，需要退避重试；</item>
    ///   <item><c>InvalidApiKey</c> —— 密钥无效。</item>
    /// </list>
    /// 认不出来的错误才原样透传，保证不丢失信息。
    /// </summary>
    private static string ExplainFailure(int statusCode, string body, string url, string protocolName)
    {
        var prefix = $"模型接口返回 {statusCode}（请求地址：{url}，协议：{protocolName}）";

        if (body.Contains("AllocationQuota.FreeTierOnly", StringComparison.OrdinalIgnoreCase)
            || body.Contains("Free quota exhausted", StringComparison.OrdinalIgnoreCase))
        {
            return prefix +
                "：账号的<b>免费额度已用尽</b>，且当前开启了「仅使用免费额度」模式，因此付费调用被拒绝。" +
                "这不是程序问题，也换模型解决不了。请到百炼控制台的模型广场 → 该模型 → " +
                "「开通服务 / 资源管理」里二选一：" +
                "① 充值后关闭「仅使用免费额度」开关；② 切换到有额度的模型。";
        }

        if (body.Contains("Throttling", StringComparison.OrdinalIgnoreCase)
            || body.Contains("RateLimit", StringComparison.OrdinalIgnoreCase))
        {
            return prefix + "：触发了模型侧的<b>限流</b>。请稍等几秒后重试，或降低并发提交频率。";
        }

        if (body.Contains("InvalidApiKey", StringComparison.OrdinalIgnoreCase)
            || body.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return prefix + "：<b>API Key 无效或未授权</b>。请到模型配置里检查密钥是否填错、是否已失效。";
        }

        return $"{prefix}：{Truncate(body, 300)}";
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

    /// <summary>
    /// 进度区间映射器。
    ///
    /// 生成是分阶段的（解析接口 → 提交 → 等待模型 → 落盘），而每一阶段只知道自己内部
    /// 完成了多少（0 - 1）。用这个类把"局部比例"折算成整条任务的百分比，
    /// 调用方就不需要到处算区间、也不容易把进度条算反。
    /// </summary>
    private sealed class ProgressScope(IProgress<ImageGenerationProgress> progress, double from, double to)
    {
        /// <summary>取一个子区间，供下一层继续细分</summary>
        public ProgressScope Slice(double childFrom, double childTo) =>
            new(progress, from + (to - from) * childFrom, from + (to - from) * childTo);

        /// <param name="ratio">本区间内的完成比例，0 - 1</param>
        public void ReportRatio(double ratio, string? stage) =>
            progress.Report(new ImageGenerationProgress(
                (int)Math.Round(100 * (from + (to - from) * Math.Clamp(ratio, 0, 1))),
                stage));
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

    /// <summary>
    /// 按协议构造并发送一次生成请求，返回响应（调用方负责释放）与响应体。
    ///
    /// 两种提交形态由协议自己声明：
    /// <list type="bullet">
    ///   <item>JSON —— 文生图，以及把参考图转成 data URI 的协议（百炼多模态）；</item>
    ///   <item>multipart —— OpenAI 系的图生图，图片必须以文件字段提交。</item>
    /// </list>
    /// </summary>
    /// <param name="minimal">为真时只携带必需字段，用于被 400 拒绝后的降级重试</param>
    private async Task<(HttpResponseMessage Response, string Body)> SendAsync(
        ImageGenerationRequest request,
        string url,
        IModelDeliveryProtocol protocol,
        ImageDeliveryRequest deliveryRequest,
        bool multipart,
        bool minimal,
        CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);

        if (multipart)
        {
            var form = minimal
                ? protocol.BuildMinimalMultipartContent(deliveryRequest) ?? protocol.BuildMultipartContent(deliveryRequest)
                : protocol.BuildMultipartContent(deliveryRequest);

            if (form is null)
                throw new InvalidOperationException(
                    $"协议 {protocol.Name} 声明需要 multipart 提交（请求地址：{url}），但没有构造出表单内容。");

            httpRequest.Content = form;
        }
        else
        {
            var requestBody = minimal
                ? protocol.BuildMinimalRequestBody(deliveryRequest)
                : protocol.BuildRequestBody(deliveryRequest);

            httpRequest.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
        }

        if (!string.IsNullOrWhiteSpace(request.Model.ApiKey))
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.Model.ApiKey.Trim());

        foreach (var (key, value) in protocol.BuildExtraHeaders(deliveryRequest))
            httpRequest.Headers.TryAddWithoutValidation(key, value);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            // HttpClient 会把「上传请求体时连接被对端掐断」这类传输层异常统一包装成
            // "Error while copying content to a stream."，完全看不出真实原因。
            // 直接把这句话写进 ErrorMessage，用户只会看到一句无从下手的英文，
            // 排查时也拿不到任何线索。这里把最内层的真实异常挖出来重写。
            throw new InvalidOperationException(DescribeTransportFailure(url, exception), exception);
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return (response, body);
    }

    /// <summary>
    /// 把传输层异常翻译成能直接指导排查的中文说明。
    ///
    /// 图生图走 multipart，参考图动辄几 MB，对端（WAF / 网关 / 模型服务）
    /// 可能在收到请求体之前就断开连接；这类问题如果不翻译，
    /// 最终只会以 "Error while copying content to a stream." 的形式呈现在任务列表里。
    /// </summary>
    private static string DescribeTransportFailure(string url, Exception exception)
    {
        var root = exception;
        while (root.InnerException is not null)
            root = root.InnerException;

        var detail = root switch
        {
            // 10054 = 对方在传输过程中强制关闭连接。绝大多数是 WAF / 网关在上传完成前
            // 就按体积或规则掐断，而不是模型接口真的坏了。
            SocketException { SocketErrorCode: var code } => $"连接被对方中断（{code}）",
            IOException => "连接在传输过程中被关闭",
            _ => root.Message,
        };

        return $"调用模型接口失败：向 {url} 发送请求时 {detail}。" +
               "若为图生图，可能是参考图体积过大触发了网关限制，请换一张更小的参考图后重试；" +
               $"底层原始错误：{root.Message}";
    }

    /// <summary>
    /// 把任务里的参考图（<see cref="TaskAsset" />，Url 形如 <c>/uploads/2026/10/17/xxx.png</c>）
    /// 读成字节，供协议层构造 multipart 文件或 data URI。
    ///
    /// 取不到的图会被跳过而不是让整条任务失败——少一张参考图还能出图，
    /// 但若因此整单失败，用户会以为是模型坏了。跳过时留一条 warning 便于回溯。
    /// </summary>
    private async Task<IReadOnlyList<ImageDeliveryReference>> ResolveReferenceImagesAsync(
        ImageGenerationRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ReferenceImages is not { Count: > 0 } assets)
            return [];

        var references = new List<ImageDeliveryReference>(assets.Count);

        foreach (var asset in assets.Take(ImageDeliveryRequest.MaxReferenceImages))
        {
            var loaded = await LoadReferenceImageAsync(asset, cancellationToken);
            if (loaded is not null)
                references.Add(loaded);
        }

        if (references.Count == 0)
            _logger.LogWarning("任务 {TaskId} 带了参考图，但一张都没能读到，本次将按文生图执行", request.TaskId);

        return references;
    }

    private async Task<ImageDeliveryReference?> LoadReferenceImageAsync(
        TaskAsset asset,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(asset.Url))
            return null;

        try
        {
            // 1) 本站 /uploads 下的文件：直接走存储层读，与 Api 落盘共用同一根目录
            if (LocalStorageDefaults.TryMapPublicUrlToRelativePath(asset.Url, out var relativePath))
            {
                await using var stream = await _fileStorage.GetAsync(relativePath, cancellationToken);
                if (stream is null)
                {
                    _logger.LogWarning("参考图 {Url} 在本站存储中不存在，已跳过", asset.Url);
                    return null;
                }

                var localBytes = await ReadAllAsync(stream, cancellationToken);
                return new ImageDeliveryReference(
                    asset.Url,
                    localBytes,
                    GuessContentType(asset.FileName ?? asset.Url),
                    Path.GetFileName(asset.FileName ?? asset.Url));
            }

            // 2) 外部 http(s) 地址：下载一次
            if (Uri.TryCreate(asset.Url, UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https")
            {
                using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("参考图 {Url} 下载失败（HTTP {Status}），已跳过", asset.Url, (int)response.StatusCode);
                    return null;
                }

                var remoteBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                return new ImageDeliveryReference(
                    asset.Url,
                    remoteBytes,
                    response.Content.Headers.ContentType?.MediaType ?? GuessContentType(asset.Url),
                    Path.GetFileName(uri.AbsolutePath));
            }

            _logger.LogWarning("参考图地址 {Url} 既不是本站文件也不是可下载的 http 地址，已跳过", asset.Url);
            return null;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "读取参考图 {Url} 失败，已跳过", asset.Url);
            return null;
        }
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    /// <summary>按扩展名推断 Content-Type，推断不出时按 PNG 处理</summary>
    private static string GuessContentType(string? nameOrUrl)
    {
        var extension = Path.GetExtension(nameOrUrl ?? string.Empty).ToLowerInvariant();

        return extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".avif" => "image/avif",
            _ => "image/png",
        };
    }

    /// <summary>
    /// 轮询任务结果，直到 SUCCEEDED 或失败/超时。
    /// 返回的是任务查询接口的最终响应体，交给 <see cref="ExtractImageAsync"/> 统一解析。
    /// </summary>
    private async Task<string> PollTaskUntilCompletedAsync(
        ImageGenerationRequest request,
        IModelDeliveryProtocol protocol,
        string statusUrl,
        ProgressScope? progress,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PollTimeout);

        // 异步模型（wan / wanx）出图要几十秒。这里按"已等待时长 / 超时上限"把进度
        // 从 30% 平滑推到 85%，让用户看到任务确实在往前走，而不是卡在原地。
        var startedAt = Stopwatch.StartNew();

        try
        {
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();

                var waited = startedAt.Elapsed.TotalSeconds / PollTimeout.TotalSeconds;
                progress?.ReportRatio(0.3 + 0.55 * Math.Clamp(waited, 0, 1), "模型正在绘制，等待返回");

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
            // 百炼图片翻译（qwen-mt-image）：同步返回 output.image_url，
            // 异步任务成功后同样把结果放在 output.image_url（不是 results 数组）。
            if (output.TryGetProperty("image_url", out var translatedUrl)
                && translatedUrl.ValueKind == JsonValueKind.String
                && translatedUrl.GetString() is { Length: > 0 } imageUrlValue)
                return await DownloadAsync(imageUrlValue, cancellationToken);

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
