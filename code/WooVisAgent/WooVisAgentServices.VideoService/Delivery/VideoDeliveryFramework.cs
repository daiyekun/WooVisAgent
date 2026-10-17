using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using WooVisAgent.Services.Common.Contracts;
using WooVisAgent.Services.Common.Storage;
using WooVisAgent.SharedLib.Delivery;
using WooVisAgent.SharedLib.Diagnostics;
using WooVisAgent.SharedLib.Domain;
using WooVisAgent.SharedLib.Ids;
using WooVisAgentServices.ImageService.Contracts;
using WooVisAgentServices.VideoService.Contracts;
using WooVisAgentServices.VideoService.Support;

namespace WooVisAgentServices.VideoService.Delivery;

/// <summary>
/// 视频交付框架实现：把「一次视频生成」编排成提交 → 轮询 → 下载落盘三步。
///
/// 与图片侧的 <c>ModelDeliveryFramework</c> 是同一套结构，但有三处视频特有的处理：
/// <list type="number">
///   <item><b>产物形态有三种</b>（URL / 二进制 content / base64），
///         响应体里没有地址时不能按字符串解析，否则报出莫名其妙的错；</item>
///   <item><b>轮询窗口以分钟计</b>（默认 15 分钟），且要把模型的排队时间算在内，
///         图片的 3 分钟窗口对视频远远不够；</item>
///   <item><b>模型侧地址会过期</b>（百炼的 video_url 只有 24 小时），
///         所以拿到地址后必须立刻转存，而不是把地址原样交给前端。</item>
/// </list>
/// </summary>
public sealed class VideoDeliveryFramework(
    IHttpClientFactory httpClientFactory,
    IFileStorageService fileStorage,
    IVideoMediaInspector mediaInspector,
    ISnowflakeIdGenerator idGenerator,
    ILogger<VideoDeliveryFramework> logger) : IVideoDeliveryFramework
{
    /// <summary>轮询上限。视频生成通常 1-5 分钟，15 分钟能覆盖绝大多数模型且不会无限挂起。</summary>
    public const int MaxPollMinutes = 15;

    /// <summary>轮询间隔。百炼建议 15 秒左右，太频繁会被限流；OpenAI 建议 2-5 秒，取 3 秒折中。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private const int MaxVideoBytes = 512 * 1024 * 1024;

    public async Task<IReadOnlyList<TaskAsset>> GenerateVideoAsync(
        VideoGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        var spec = VideoDeliveryResolver.Resolve(request.Model)
                   ?? throw new InvalidOperationException(
                       $"无法确定模型「{request.Model.Name}」的视频接口形态，请检查模型配置里的接口地址与交付模板。");

        var submitUrl = spec.ResolveSubmitUrl(request.Model)
                        ?? throw new InvalidOperationException(
                            $"模型「{request.Model.Name}」未配置接口地址（endpoint），无法提交视频生成任务。");

        request.Progress?.Report(new GenerationProgress(8, "正在提交到视频模型"));

        var client = httpClientFactory.CreateClient(DependencyInjection.VideoGenerationHttpClientName);

        // 首帧图要转成模型能取到的形式：本站 /uploads 下的文件对云端不可见，
        // 必须先读成字节再转 data URI，否则百炼 / OpenAI 拉不到图，
        // 产出的视频和首帧毫无关系。
        var firstFrame = request.IsImageToVideo
            ? await ResolveFirstFrameAsync(request.FirstFrame!, client, cancellationToken)
            : null;

        var deliveryRequest = new VideoDeliveryRequest(
            request.Model,
            request.Prompt,
            request.NegativePrompt,
            firstFrame,
            request.Resolution,
            request.DurationSeconds,
            request.Fps,
            request.Seed,
            request.ExtraParams);

        var body = VideoRequestBuilder.BuildSubmitBody(deliveryRequest, spec);

        var (taskId, submitBody) = await SubmitAsync(client, spec, request, submitUrl, body, cancellationToken);

        request.Progress?.Report(new GenerationProgress(
            20,
            $"已提交到模型（任务号 {Shorten(taskId)}），等待生成"));

        // 同步协议：提交响应里直接带产物，不必轮询
        if (string.IsNullOrWhiteSpace(spec.StatusPath))
        {
            var inline = ResolveOutput(spec, submitBody);
            return await SaveOutputAsync(request, client, spec, inline, taskId, cancellationToken);
        }

        var resultBody = await PollUntilCompletedAsync(client, spec, request, taskId, cancellationToken);
        var output = ResolveOutput(spec, resultBody);

        return await SaveOutputAsync(request, client, spec, output, taskId, cancellationToken);
    }

    public async Task<VideoDeliveryProbeResult> TestConnectivityAsync(
        ModelConfig model,
        CancellationToken cancellationToken = default)
    {
        var spec = VideoDeliveryResolver.Resolve(model);

        if (spec is null)
            return new VideoDeliveryProbeResult(false, null, "无法解析该模型的视频接口形态，请检查接口地址与交付模板。", null);

        var url = spec.ResolveSubmitUrl(model);

        if (url is null)
            return new VideoDeliveryProbeResult(false, null, "模型未配置接口地址（endpoint）。", null);

        var client = httpClientFactory.CreateClient(DependencyInjection.VideoGenerationHttpClientName);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyAuthentication(request, model);

            using var response = await client.SendAsync(request, cancellationToken);

            stopwatch.Stop();

            // 405/401/403 说明地址是通的、只是方法或密钥不对——
            // 对「连通性测试」而言这已经是成功，不必报红。
            var reachable = response.IsSuccessStatusCode
                            || response.StatusCode is HttpStatusCode.MethodNotAllowed
                                or HttpStatusCode.Unauthorized
                                or HttpStatusCode.Forbidden
                                or HttpStatusCode.BadRequest;

            var message = reachable
                ? $"接口可达（HTTP {(int)response.StatusCode}，预设 {spec.PresetName}）"
                : Truncate(await SafeReadAsync(response, cancellationToken), 300);

            return new VideoDeliveryProbeResult(
                reachable,
                (int)stopwatch.ElapsedMilliseconds,
                message,
                url);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            stopwatch.Stop();
            return new VideoDeliveryProbeResult(false, (int)stopwatch.ElapsedMilliseconds, DescribeTransportFailure(url, exception), url);
        }
    }

    /// <summary>提交生成任务，返回 (模型任务号, 响应正文)</summary>
    private async Task<(string TaskId, string Body)> SubmitAsync(
        HttpClient client,
        VideoDeliverySpec spec,
        VideoGenerationRequest request,
        string url,
        string body,
        CancellationToken cancellationToken)
    {
        var operation = request.IsImageToVideo ? "image2video" : "text2video";

        using var llmSpan = TraceSources.ModelSource.StartLlmCall(
            operation,
            provider: request.Model.Provider ?? "unknown",
            model: request.Model.ModelKey ?? request.Model.Name ?? "unknown",
            prompt: request.Prompt,
            endpoint: url,
            // 复用该 tag 表示「带了参考素材」，图生视频比文生视频慢得多
            hasReferenceImages: request.IsImageToVideo);

        llmSpan?.SetTag("wv.delivery.preset", spec.PresetName);
        llmSpan?.SetTag("wv.delivery.output_kind", spec.OutputKind.ToString());
        llmSpan?.SetTag("wv.delivery.request_body", Truncate(body, 500));

        using var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };

        ApplyAuthentication(message, request.Model);
        ApplyExtraHeaders(message, spec);

        try
        {
            using var response = await client.SendAsync(message, cancellationToken);
            var responseBody = await SafeReadAsync(response, cancellationToken);

            llmSpan?.SetTag("wv.delivery.status_code", (int)response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                llmSpan?.SetTag("wv.delivery.error_body", Truncate(responseBody, 500));

                logger.LogError(
                    "视频模型接口返回 {StatusCode}。请求地址={Url}，预设={Preset}，响应={Body}",
                    (int)response.StatusCode,
                    url,
                    spec.PresetName,
                    Truncate(responseBody, 500));

                throw new InvalidOperationException(ExplainFailure((int)response.StatusCode, responseBody, url, spec));
            }

            llmSpan.RecordSuccess();

            var taskId = JsonNodePath.GetScalar(JsonNodePath.Parse(responseBody), spec.TaskIdPath);

            if (string.IsNullOrWhiteSpace(taskId))
            {
                // 同步协议可能没有 task_id（产物直接在响应里），这种情况不算错
                if (!string.IsNullOrWhiteSpace(spec.StatusPath))
                {
                    throw new InvalidOperationException(
                        $"视频模型返回的响应里找不到任务号（按路径 {spec.TaskIdPath} 读取，地址：{url}）。" +
                        $"请检查模型配置的交付模板是否与该接口匹配。响应：{Truncate(responseBody, 300)}");
                }

                return (string.Empty, responseBody);
            }

            logger.LogInformation(
                "任务 {TaskId} 已提交到视频模型，模型任务号 {ModelTaskId}，轮询地址 {StatusUrl}",
                request.TaskId,
                taskId,
                spec.ResolveStatusUrl(request.Model, taskId));

            return (taskId, responseBody);
        }
        catch (HttpRequestException exception)
        {
            llmSpan.RecordFailure(exception);
            throw new InvalidOperationException(DescribeTransportFailure(url, exception), exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            var failure = new TimeoutException($"调用视频模型接口 {url} 超时。");
            llmSpan.RecordFailure(failure);
            throw new InvalidOperationException(DescribeTransportFailure(url, exception), exception);
        }
    }

    /// <summary>
    /// 轮询任务状态直到成功 / 失败 / 超时。
    ///
    /// 进度按已耗时线性推进到 90%，剩下的 10% 留给下载与落盘——
    /// 这样用户看到的进度条是持续移动的，而不是卡在某个百分比不动。
    /// </summary>
    private async Task<string> PollUntilCompletedAsync(
        HttpClient client,
        VideoDeliverySpec spec,
        VideoGenerationRequest request,
        string taskId,
        CancellationToken cancellationToken)
    {
        var statusUrl = spec.ResolveStatusUrl(request.Model, taskId)
                        ?? throw new InvalidOperationException(
                            $"无法构造视频任务的查询地址（预设 {spec.PresetName}，模型接口地址：{request.Model.Endpoint}）。");

        var usePost = spec.StatusMethod.Equals("POST", StringComparison.OrdinalIgnoreCase);
        var failedStatuses = ParseStatuses(spec.FailedStatuses);
        var pollStartedAt = DateTimeOffset.UtcNow;
        var deadline = pollStartedAt.AddMinutes(MaxPollMinutes);
        var attempt = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var body = usePost
                ? await PollWithPostAsync(client, spec, request, statusUrl, taskId, cancellationToken)
                : await PollWithGetAsync(client, spec, request, statusUrl, cancellationToken);

            var status = JsonNodePath.GetScalar(JsonNodePath.Parse(body), spec.StatusValuePath);

            if (string.IsNullOrWhiteSpace(status))
            {
                // 有些接口在未就绪时不返回状态字段，这本身不算失败，继续等
                logger.LogDebug("任务 {TaskId} 的状态响应里没有 {Path} 字段，继续轮询", request.TaskId, spec.StatusValuePath);
            }
            else if (status.Equals(spec.SucceededStatus, StringComparison.OrdinalIgnoreCase))
            {
                var elapsedRatio = Math.Clamp(
                    (double)(DateTimeOffset.UtcNow - pollStartedAt).TotalMinutes / MaxPollMinutes,
                    0,
                    1);

                request.Progress?.Report(new GenerationProgress(
                    20 + (int)(70 * elapsedRatio),
                    "模型已生成完成，正在下载视频"));

                return body;
            }
            else if (failedStatuses.Contains(status))
            {
                var reason = JsonNodePath.GetScalar(JsonNodePath.Parse(body), spec.ErrorPath);

                throw new InvalidOperationException(
                    $"视频生成失败（模型返回状态：{status}）" +
                    (string.IsNullOrWhiteSpace(reason) ? "。" : $"：{Truncate(reason, 500)}"));
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"视频生成超过 {MaxPollMinutes} 分钟仍未完成，已停止等待。" +
                    "若是模型排队时间较长，请稍后到任务列表重试，或换一个出片更快的模型。");
            }

            attempt++;

            // 每 10 次（约 30 秒）上报一次进度，避免轮询期间前端完全没动静
            if (attempt % 10 == 0)
            {
                var ratio = Math.Clamp(
                    (double)(DateTimeOffset.UtcNow - pollStartedAt).TotalMinutes / MaxPollMinutes,
                    0,
                    1);

                request.Progress?.Report(new GenerationProgress(
                    20 + (int)(70 * ratio),
                    $"模型仍在生成中（已等待 {attempt * PollInterval.TotalSeconds:F0} 秒）"));
            }

            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    private async Task<string> PollWithGetAsync(
        HttpClient client,
        VideoDeliverySpec spec,
        VideoGenerationRequest request,
        string url,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyAuthentication(message, request.Model);

        return await SendPollAsync(client, spec, request, message, url, cancellationToken);
    }

    private async Task<string> PollWithPostAsync(
        HttpClient client,
        VideoDeliverySpec spec,
        VideoGenerationRequest request,
        string url,
        string taskId,
        CancellationToken cancellationToken)
    {
        // SiliconFlow 这类接口要求 POST 到固定地址并在 body 里带任务号，
        // 且明确不接受把 id 拼进 URL——这是做成可配置 StatusMethod 的原因。
        var payload = string.IsNullOrWhiteSpace(spec.StatusIdField)
            ? taskId
            : JsonSerializer.Serialize(new Dictionary<string, string> { [spec.StatusIdField] = taskId });

        using var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
        };

        ApplyAuthentication(message, request.Model);

        return await SendPollAsync(client, spec, request, message, url, cancellationToken);
    }

    private async Task<string> SendPollAsync(
        HttpClient client,
        VideoDeliverySpec spec,
        VideoGenerationRequest request,
        HttpRequestMessage message,
        string url,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.SendAsync(message, cancellationToken);
            var body = await SafeReadAsync(response, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"查询视频任务状态失败（HTTP {(int)response.StatusCode}，地址：{url}）：{Truncate(body, 300)}");
            }

            return body;
        }
        catch (HttpRequestException exception)
        {
            throw new InvalidOperationException(DescribeTransportFailure(url, exception), exception);
        }
    }

    /// <summary>从响应里取出视频产物（URL / base64 / 或者什么都没取到需要走 content 下载）</summary>
    private VideoOutput ResolveOutput(VideoDeliverySpec spec, string body)
    {
        var root = JsonNodePath.Parse(body);

        return new VideoOutput(
            JsonNodePath.GetScalar(root, spec.VideoUrlPath),
            JsonNodePath.GetScalar(root, "content"),
            JsonNodePath.GetScalar(root, "video"),
            body);
    }

    /// <summary>把产物下载下来、存进本站存储、抽封面、补齐元信息</summary>
    private async Task<IReadOnlyList<TaskAsset>> SaveOutputAsync(
        VideoGenerationRequest request,
        HttpClient client,
        VideoDeliverySpec spec,
        VideoOutput output,
        string taskId,
        CancellationToken cancellationToken)
    {
        request.Progress?.Report(new GenerationProgress(92, "正在下载并保存视频"));

        var (bytes, extension) = await DownloadAsync(client, spec, request, output, taskId, cancellationToken);

        if (bytes.Length == 0)
        {
            throw new InvalidOperationException(
                "模型返回了任务成功，但没能取到视频内容。" +
                "常见原因：交付模板里的产物类型（outputKind）与该接口不符——" +
                "有些接口在响应体里直接给视频地址，有些要另外调 content 接口下载。");
        }

        var fileName = $"video-{request.TaskId}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.{extension}";

        var url = await SaveToStorageAsync(bytes, fileName, cancellationToken);

        var asset = new TaskAsset
        {
            Id = idGenerator.NextId().ToString(CultureInfo.InvariantCulture),
            Type = "video",
            Url = url,
            FileName = fileName,
            FileSize = bytes.Length,
        };

        // 封面与元信息是锦上添花：取不到就留空，不能因此让整条任务失败
        try
        {
            await EnrichMediaInfoAsync(asset, bytes, extension, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "任务 {TaskId} 的视频已保存，但补齐封面 / 元信息时出错", request.TaskId);
        }

        return [asset];
    }

    /// <summary>按产物类型取回视频字节</summary>
    private async Task<(byte[] Bytes, string Extension)> DownloadAsync(
        HttpClient client,
        VideoDeliverySpec spec,
        VideoGenerationRequest request,
        VideoOutput output,
        string taskId,
        CancellationToken cancellationToken)
    {
        switch (spec.OutputKind)
        {
            case VideoOutputKind.ContentBinary:
            {
                var contentUrl = spec.ResolveContentUrl(request.Model, taskId)
                                 ?? throw new InvalidOperationException(
                                     "该模型的视频产物需要通过 content 接口下载，但交付模板里没有配置 contentPath。");

                var bytes = await DownloadBytesAsync(client, request.Model, contentUrl, cancellationToken);
                return (bytes, GuessExtension(contentUrl, bytes));
            }

            case VideoOutputKind.Base64:
            {
                var encoded = output.Base64
                              ?? throw new InvalidOperationException(
                                  "交付模板声明产物是 base64，但响应里没有取到内容字段。");

                return (Convert.FromBase64String(StripDataUrlPrefix(encoded)), "mp4");
            }

            default:
            {
                var url = output.Url
                          ?? throw new InvalidOperationException(
                              "响应里没有取到视频地址。" +
                              $"请检查交付模板里 videoUrlPath 的取值是否正确（当前：{spec.VideoUrlPath}）。");

                var bytes = await DownloadBytesAsync(client, request.Model, url, cancellationToken);
                return (bytes, GuessExtension(url, bytes));
            }
        }
    }

    private async Task<byte[]> DownloadBytesAsync(
        HttpClient client,
        ModelConfig model,
        string url,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, url);

        // 百炼 / OpenAI 的下载地址通常带签名，鉴权头多余；但同一地址若要求走 Bearer 也能工作
        ApplyAuthentication(message, model);

        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"下载视频失败（HTTP {(int)response.StatusCode}，地址：{url}）。" +
                "若地址带有时效性签名，可能是排队时间过久导致链接过期，请重试。");
        }

        var length = response.Content.Headers.ContentLength;

        if (length is > MaxVideoBytes)
        {
            throw new InvalidOperationException(
                $"视频体积 {length / 1024 / 1024} MB 超过上限 {MaxVideoBytes / 1024 / 1024} MB，已放弃下载。");
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    /// <summary>
    /// 把首帧图转成模型可访问的形式。
    ///
    /// 本站 /uploads 下的地址对云端是不可见的，必须转成 data URI；
    /// 已经是公网地址的原样使用（转成 base64 反而会让请求体白涨几 MB）。
    /// </summary>
    private async Task<string?> ResolveFirstFrameAsync(
        TaskAsset frame,
        HttpClient client,
        CancellationToken cancellationToken)
    {
        var url = frame.Url;

        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https")
        {
            // 若是本站域名，外部模型同样拉不到，仍需转 data URI
            if (!IsLocalHost(absolute))
                return url;
        }

        var bytes = await ReadLocalOrRemoteBytesAsync(url, client, cancellationToken);

        if (bytes is null)
        {
            logger.LogWarning("首帧图 {Url} 读取失败，本次将按文生视频执行", url);
            return null;
        }

        var contentType = GuessImageContentType(frame.FileName ?? url);
        return $"data:{contentType};base64,{Convert.ToBase64String(bytes)}";
    }

    private async Task<byte[]?> ReadLocalOrRemoteBytesAsync(
        string url,
        HttpClient client,
        CancellationToken cancellationToken)
    {
        if (LocalStorageDefaults.TryMapPublicUrlToRelativePath(url, out var relativePath))
        {
            await using var stream = await fileStorage.GetAsync(relativePath, cancellationToken);

            if (stream is null)
                return null;

            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            return buffer.ToArray();
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
                return null;

            return await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }

        return null;
    }

    private static bool IsLocalHost(Uri uri)
    {
        if (uri.IsLoopback)
            return true;

        var host = uri.Host;

        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
               || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
               || string.Equals(host, "0.0.0.0", StringComparison.Ordinal);
    }

    private async Task EnrichMediaInfoAsync(
        TaskAsset asset,
        byte[] bytes,
        string extension,
        CancellationToken cancellationToken)
    {
        // 先落临时文件：ffprobe / ffmpeg 需要真实路径，且探测完就该删
        var tempDirectory = Path.Combine(Path.GetTempPath(), "woovideo", asset.Id);
        Directory.CreateDirectory(tempDirectory);

        var tempVideo = Path.Combine(tempDirectory, $"probe.{extension}");
        var tempThumb = Path.Combine(tempDirectory, "cover.jpg");

        try
        {
            await File.WriteAllBytesAsync(tempVideo, bytes, cancellationToken);

            var info = await mediaInspector.ProbeAsync(tempVideo, cancellationToken);

            if (info is not null)
            {
                asset.Duration = info.DurationSeconds;
                asset.Width = info.Width;
                asset.Height = info.Height;
            }

            var thumbnail = await mediaInspector.ExtractThumbnailAsync(tempVideo, tempThumb, cancellationToken);

            if (thumbnail is not null && File.Exists(thumbnail))
            {
                await using var stream = File.OpenRead(thumbnail);
                var thumbUrl = await fileStorage.SaveAsync(stream, $"video-{asset.Id}.jpg", cancellationToken);
                asset.ThumbnailUrl = thumbUrl;
            }
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    private async Task<string> SaveToStorageAsync(byte[] bytes, string fileName, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream(bytes);
        return await fileStorage.SaveAsync(buffer, fileName, cancellationToken);
    }

    private static void ApplyAuthentication(HttpRequestMessage message, ModelConfig model)
    {
        if (!string.IsNullOrWhiteSpace(model.ApiKey))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", model.ApiKey.Trim());
    }

    private static void ApplyExtraHeaders(HttpRequestMessage message, VideoDeliverySpec spec)
    {
        foreach (var (key, value) in spec.ExtraHeaders)
        {
            // 百炼缺少这个头会直接报 "does not support synchronous calls"
            if (!string.IsNullOrWhiteSpace(key) && !message.Headers.Contains(key))
                message.Headers.TryAddWithoutValidation(key, value);
        }
    }

    private static HashSet<string> ParseStatuses(string? csv)
        => new(
            (csv ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 把云端错误翻译成可读的中文。
    /// 英文原文里完全不提示「该做什么」的错误（免费额度、限流、审核）在这里落地成人话。
    /// </summary>
    private static string ExplainFailure(int statusCode, string body, string url, VideoDeliverySpec spec)
    {
        var prefix = $"视频模型接口返回 {statusCode}（地址：{url}，预设：{spec.PresetName}）";

        if (body.Contains("AllocationQuota.FreeTierOnly", StringComparison.OrdinalIgnoreCase)
            || body.Contains("Free quota exhausted", StringComparison.OrdinalIgnoreCase))
        {
            return prefix +
                "：账号的免费额度已用尽，且当前开启了「仅使用免费额度」模式。" +
                "请到百炼控制台的模型广场 → 该模型 → 开通服务里二选一：" +
                "① 充值后关闭「仅使用免费额度」；② 换一个还有额度的模型。";
        }

        if (body.Contains("Throttling", StringComparison.OrdinalIgnoreCase)
            || body.Contains("RateLimit", StringComparison.OrdinalIgnoreCase)
            || body.Contains("429", StringComparison.Ordinal))
        {
            return prefix + "：触发了模型侧限流。请稍等几秒后重试，或降低并发提交频率。";
        }

        if (body.Contains("InvalidApiKey", StringComparison.OrdinalIgnoreCase)
            || statusCode == (int)HttpStatusCode.Unauthorized)
        {
            return prefix + "：密钥无效或没有访问该模型的权限。请检查模型配置里的 API Key 与模型权限。";
        }

        if (body.Contains("not_supported", StringComparison.OrdinalIgnoreCase)
            || body.Contains("ModelNotSupported", StringComparison.OrdinalIgnoreCase))
        {
            return prefix + "：该模型不支持当前请求。" +
                "常见原因：该模型只支持图生视频（或只支持文生视频），请检查模型配置里的能力声明。";
        }

        if (statusCode == (int)HttpStatusCode.NotFound)
        {
            var shimHint = url.Contains("/compatible-mode", StringComparison.OrdinalIgnoreCase)
                ? "当前地址里仍有 /compatible-mode——那是聊天接口的 OpenAI 兼容层，视频生成各家都只有原生入口，" +
                  "请把 endpoint 改成厂商原生域名（百炼为 https://dashscope.aliyuncs.com）。"
                : string.Empty;

            return prefix +
                "：接口地址不存在。请核对模型配置里的 endpoint 与交付模板里的 submitPath，" +
                "拼接后的完整地址应与该服务商文档一致。" + shimHint;
        }

        if (body.Contains("DataInspectionFailed", StringComparison.OrdinalIgnoreCase)
            || body.Contains("content_filter", StringComparison.OrdinalIgnoreCase))
        {
            return prefix + "：提示词未通过内容审核，请调整描述后重试。";
        }

        return prefix + "：" + Truncate(body, 500);
    }

    /// <summary>
    /// 把传输层异常翻译成人话。
    ///
    /// 上传首帧图时对端可能因体积或规则提前断开，这类问题最终只会以
    /// "Error while copying content to a stream." 的形式出现，排查价值为零。
    /// </summary>
    private static string DescribeTransportFailure(string url, Exception exception)
    {
        var root = exception;

        while (root.InnerException is not null)
            root = root.InnerException;

        var detail = root switch
        {
            SocketException { SocketErrorCode: var code } => $"连接被对方中断（{code}）",
            IOException => "连接在传输过程中被关闭",
            _ => root.Message,
        };

        return $"调用视频模型接口失败：向 {url} 发送请求时 {detail}。" +
               "若本次是图生视频，可能是首帧图体积过大触发了网关限制，请换一张更小的图后重试；" +
               $"底层原始错误：{root.Message}";
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string GuessImageContentType(string? nameOrUrl)
    {
        var extension = Path.GetExtension(nameOrUrl ?? string.Empty).ToLowerInvariant();

        return extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            _ => "image/png",
        };
    }

    /// <summary>优先按地址后缀判断扩展名，判断不出时按字节特征猜，再兜底 mp4</summary>
    private static string GuessExtension(string? nameOrUrl, byte[] bytes)
    {
        var fromUrl = Path.GetExtension(new Uri(nameOrUrl ?? "x", UriKind.RelativeOrAbsolute).AbsolutePath)
            .TrimStart('.')
            .ToLowerInvariant();

        if (fromUrl is "mp4" or "mov" or "webm" or "m4v")
            return fromUrl;

        // ISO BMFF（mp4/mov）的文件头是第 4-7 字节的 "ftyp"
        if (bytes.Length > 12 && bytes[4] == 'f' && bytes[5] == 't' && bytes[6] == 'y' && bytes[7] == 'p')
            return "mp4";

        // WebM / Matroska 的 EBML 头
        if (bytes.Length > 4 && bytes[0] == 0x1A && bytes[1] == 0x45 && bytes[2] == 0xDF && bytes[3] == 0xA3)
            return "webm";

        return string.IsNullOrEmpty(fromUrl) ? "mp4" : fromUrl;
    }

    private static string StripDataUrlPrefix(string value)
        => value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? value[(value.IndexOf(',', StringComparison.Ordinal) + 1)..]
            : value;

    private static string Shorten(string value)
        => value.Length <= 8 ? value : value[..8];

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // 临时文件删不掉不影响任务结果，交给系统清理
        }
    }

    /// <summary>从响应里取出的产物线索</summary>
    private sealed record VideoOutput(string? Url, string? Base64, string? InlineVideo, string RawBody);
}