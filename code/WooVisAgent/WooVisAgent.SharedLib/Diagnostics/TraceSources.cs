using System.Diagnostics;
using OpenTelemetry.Trace;

namespace WooVisAgent.SharedLib.Diagnostics;

/// <summary>
/// 业务链路追踪的统一入口。
///
/// 背景：项目原先只在 <c>ServiceDefaults/Extensions.cs</c> 里调用了 <c>ConfigureOpenTelemetry</c>，
/// 但那只是<b>打开采集器</b>——链路面板上一个 span 都没有，原因有四：
/// <list type="number">
///   <item>全项目没有任何 <see cref="ActivitySource" />，业务代码从不建 span，
///         采集器只拿到 ASP.NET Core 入站和 HttpClient 出站两条自动埋点</item>
///   <item>Aspire 的 project 资源没挂 <c>WithOtlpExporter()</c>，
///         缺 <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>，ServiceDefaults 里的 exporter 分支根本不会走</item>
///   <item>没有 <c>service.name</c> 资源属性，导出的 span 认不出归属</item>
///   <item>消息队列（RabbitMQ）两侧没有传播上下文，Api 与 Worker 的链路断成两半</item>
/// </list>
///
/// 用法（异常自动记录并原样上抛，不改变原有控制流）：
/// <code>
/// using var span = TraceSources.ApiSource.StartActivity("Login");
/// span.TagCurrentUser(userId);
/// span?.SetTag("user.email", email);
/// // ...业务
/// </code>
///
/// <para><b>关于大模型调用</b>：调用外部模型 API 时用 <see cref="StartLlmCall" />，
/// 它会把 <c>gen_ai.system</c> / <c>gen_ai.request.model</c> / <c>llm.prompt_chars</c>
/// 这些语义化属性一并打上，面板 Tags 页签里能直接看出「哪个模型、耗时多久、提示词多长」，
/// 而不是一堆看不出所以然的 span 名字。</para>
/// </summary>
public static class TraceSources
{
    /// <summary>Api 的 ActivitySource 名字符串，仅供 ServiceDefaults 的 AddSource 使用</summary>
    public const string ApiName = "WooVisAgent.Api";

    /// <summary>GenerateWorker 的 ActivitySource 名字符串</summary>
    public const string WorkerName = "WooVisAgent.GenerateWorker";

    /// <summary>共享库的 ActivitySource 名字符串</summary>
    public const string ModelName = "WooVisAgent.SharedLib";

    /// <summary>Api 自身的 span。<b>建 span 用这个</b>，不要用 ApiName 字符串</summary>
    public static readonly ActivitySource ApiSource = new(ApiName, SourceVersion);

    /// <summary>GenerateWorker 消费消息、执行生成的 span</summary>
    public static readonly ActivitySource WorkerSource = new(WorkerName, SourceVersion);

    /// <summary>共享库里的 span，重点是<b>大模型调用</b>与存储读写</summary>
    public static readonly ActivitySource ModelSource = new(ModelName, SourceVersion);

    /// <summary>
    /// 采集器版本。跟随程序集版本，便于在面板上区分「哪次构建产生的 span」。
    /// </summary>
    private static readonly string SourceVersion =
        typeof(TraceSources).Assembly.GetName().Version?.ToString() ?? "1.0.0";

    /// <summary>
    /// 创建一个 span。
    ///
    /// <para>返回 null 是<b>正常</b>的：只有 <c>AddSource(名字)</c> 订阅了该 ActivitySource
    /// 且当前存在监听者时才会返回对象。没有订阅时返回 null 可以省掉整棵调用栈的对象分配——
    /// 所以调用方必须容忍 null，绝不能直接 <c>span.SetTag()</c> 解引用。
    /// 上面的扩展方法全都做了 null 保护，用它们就不会踩这个坑。</para>
    /// </summary>
    public static Activity? StartActivity(
        this ActivitySource source,
        string name,
        ActivityKind kind = ActivityKind.Internal,
        Activity? parent = null)
    {
        // parent 为 null 时让 SDK 自行接续 Activity.Current，形成
        // 「入站请求 → MediatR 用例 → 大模型调用」的完整父子链。
        // 只有显式传入跨进程还原出来的上下文时才覆盖父节点。
        var activity = parent is null
            ? source.StartActivity(name, kind)
            : source.StartActivity(name, kind, parent);

        if (activity is null)
        {
            return null;
        }

        // 把链路 ID 显示出来，方便把「控制台那行日志」和「面板上那条 span」对上。
        // W3C 下 TraceId 是 32 位、SpanId 是 16 位十六进制。
        activity.SetTag("wv.trace_id", activity.TraceId.ToHexString());
        activity.SetTag("wv.span_id", activity.SpanId.ToHexString());

        return activity;
    }

    /// <summary>
    /// 记录一次<b>出站的大模型调用</b>。
    ///
    /// 语义化约定对齐 OpenTelemetry GenAI 规范，避免每个调用点自己发明属性名——
    /// 属性名统一才能在面板上按 <c>gen_ai.request.model</c> 一键筛出「所有调了某个模型的链路」。
    /// <list type="bullet">
    ///   <item><c>gen_ai.system</c> —— 供应方，如 aliyun-dashscope / openai</item>
    ///   <item><c>gen_ai.operation.name</c> —— text2image / image2image / image_translate</item>
    ///   <item><c>gen_ai.request.model</c> —— 模型名</item>
    ///   <item><c>llm.is_image_to_image</c> —— 是否带参考图。图生图比文生图慢得多，
    ///         排查「为什么这次特别慢」时这是第一个要看的字段</item>
    ///   <item><c>server.address</c> / <c>server.port</c> —— 调用终点</item>
    ///   <item><c>llm.prompt_chars</c> —— 提示词长度。只记长度不记内容：
    ///         提示词可能含用户隐私，全量入库既不安全也会让 span 体积失控</item>
    /// </list>
    /// </summary>
    public static Activity? StartLlmCall(
        this ActivitySource source,
        string operation,
        string provider,
        string model,
        string? prompt = null,
        string? endpoint = null,
        bool hasReferenceImages = false)
    {
        var span = source.StartActivity($"llm.{operation}", ActivityKind.Client);
        if (span is null)
        {
            return null;
        }

        span.SetTag("gen_ai.system", provider);
        span.SetTag("gen_ai.operation.name", operation);
        span.SetTag("gen_ai.request.model", model);
        span.SetTag("llm.is_image_to_image", hasReferenceImages);

        if (!string.IsNullOrWhiteSpace(prompt))
        {
            span.SetTag("llm.prompt_chars", prompt.Length);
        }

        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            span.SetTag("server.address", uri.Host);
            if (!uri.IsDefaultPort)
            {
                span.SetTag("server.port", uri.Port);
            }
        }
        else if (!string.IsNullOrWhiteSpace(endpoint))
        {
            span.SetTag("llm.endpoint", endpoint);
        }

        return span;
    }

    /// <summary>
    /// 记录一次异常并把 span 标记为失败，然后<b>原样重新抛出</b>。
    ///
    /// 重新抛出是关键：调用方原有的 catch、错误提示、任务失败状态全都依赖异常继续上抛，
    /// 在这里吞掉会让任务永远停在「生成中」。
    /// </summary>
    public static void RecordFailure(this Activity? span, Exception exception)
    {
        if (span is null)
        {
            return;
        }

        span.SetStatus(ActivityStatusCode.Error, exception.Message);
        span.SetTag("error.type", exception.GetType().FullName);
        span.SetTag("error.message", exception.Message);
        span.RecordException(exception);
    }

    /// <summary>
    /// 标记 span 成功。默认状态是 Unset，在面板上显示为「未设置」；
    /// 显式设成 Ok 能一眼看出「这次调用没有异常」。
    /// </summary>
    public static void RecordSuccess(this Activity? span)
    {
        span?.SetStatus(ActivityStatusCode.Ok);
    }

    /// <summary>
    /// 把当前用户 ID 打到 span 上，让「这条请求是谁发起的」可以直接在链路里查到，
    /// 不必再去翻日志按时间对齐。
    /// </summary>
    public static void TagCurrentUser(this Activity? span, string? userId)
    {
        if (span is null || string.IsNullOrEmpty(userId))
        {
            return;
        }

        span.SetTag("enduser.id", userId);
    }

    /// <summary>
    /// 记录一次<b>入站消息</b>（RabbitMQ 消费）。
    ///
    /// <paramref name="parentContext" /> 是消息头里带过来的上下文——由
    /// <see cref="InjectContext" /> 在生产端写入。带上它，Api 提交任务的那条链路
    /// 和 Worker 实际生成的那条链路在面板上才会连成一棵完整的树；
    /// 不带的话就是两条互不相干的链路，看不出「这条生成任务是谁提交的」。
    /// </summary>
    public static Activity? StartMessageConsume(
        this ActivitySource source,
        string operation,
        string messageType,
        ActivityContext? parentContext = null,
        string? messageId = null)
    {
        // 有上游上下文就挂到它下面，Api 的提交链路与 Worker 的执行链路才会连成一棵树。
        var span = parentContext is { } ctx
            ? source.StartActivity($"consume {operation}", ActivityKind.Consumer, ctx)
            : source.StartActivity($"consume {operation}", ActivityKind.Consumer);

        if (span is null)
        {
            return null;
        }

        // 对齐 OpenTelemetry messaging 语义约定，面板上可直接按这些字段筛选。
        span.SetTag("messaging.system", "rabbitmq");
        span.SetTag("messaging.operation.type", "process");
        span.SetTag("messaging.operation.name", operation);
        span.SetTag("messaging.message.type", messageType);

        if (!string.IsNullOrEmpty(messageId))
        {
            span.SetTag("messaging.message.id", messageId);
        }

        return span;
    }

    /// <summary>
    /// 把当前上下文写进消息头，供消费端 <see cref="StartMessageConsume" /> 还原。
    ///
    /// 返回的字典要作为消息的 Header 一起发出去。没有这一步，
    /// 跨进程链路必然断开——这是分布式追踪最常见的失效点。
    /// </summary>
    public static Dictionary<string, string> InjectContext()
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);

        var activity = Activity.Current;
        if (activity is null)
        {
            return headers;
        }

        // W3C TraceContext 传播格式：traceparent 是规范字段，tracestate 是补充。
        headers["traceparent"] = $"00-{activity.TraceId.ToHexString()}-{activity.SpanId.ToHexString()}-{(activity.Recorded ? "01" : "00")}";
        headers["tracestate"] = activity.TraceStateString ?? string.Empty;

        return headers;
    }

    /// <summary>
    /// 从消息头里解析出上游上下文。
    ///
    /// 解析失败返回 null 而不是抛异常——消息头可能来自旧版本客户端、
    /// 或被人手工改过，这时应当开一条<b>新的</b>链路，而不是让整条消息消费失败。
    /// </summary>
    public static ActivityContext? ExtractContext(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0)
        {
            return null;
        }

        if (!headers.TryGetValue("traceparent", out var traceParent) || string.IsNullOrWhiteSpace(traceParent))
        {
            return null;
        }

        // W3C 格式：00-{32 位 traceId}-{16 位 spanId}-{2 位 flags}
        var parts = traceParent.Split('-');
        if (parts.Length < 4 || parts[1].Length != 32 || parts[2].Length != 16)
        {
            return null;
        }

        // TryParse 失败返回 default（traceId 全 0），据此判定无效
        var traceId = ActivityTraceId.CreateFromString(parts[1].AsSpan());
        var spanId = ActivitySpanId.CreateFromString(parts[2].AsSpan());

        if (traceId == default || spanId == default)
        {
            return null;
        }

        var traceFlags = parts[3] == "01"
            ? ActivityTraceFlags.Recorded
            : ActivityTraceFlags.None;

        return new ActivityContext(traceId, spanId, traceFlags);
    }
}
