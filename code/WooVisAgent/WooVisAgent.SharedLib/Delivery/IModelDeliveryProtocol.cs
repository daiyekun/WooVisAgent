using WooVisAgent.SharedLib.Domain;

namespace WooVisAgent.SharedLib.Delivery;

/// <summary>
/// 模型交付协议：决定「一次模型调用到底应该打向哪个 URL、用哪种请求体、是否要轮询结果」。
///
/// 背景：早期实现把 endpoint 硬编码拼接成 <c>{endpoint}/v1/images/generations</c>，
/// 导致非 OpenAI 协议的模型返回 404。后续修复又发现，只统一「路径」还不够——
/// 各家的 <b>请求体结构</b>（OpenAI 顶层 prompt vs DashScope 的 input.prompt）
/// 与<b>调用方式</b>（同步返回 vs 提交任务后轮询）也各不相同。
/// 因此本接口把这三件事一起抽象出来。
///
/// 该抽象放在 SharedLib，供 ModelService（连通性测试）与 ImageService（图片生成）共用，
/// 避免在两个服务里各写一套路径/请求体拼接逻辑。
/// </summary>
public interface IModelDeliveryProtocol
{
    /// <summary>协议名称，用于日志与排查（如 openai-images / aliyun-wanx / aliyun-compatible）</summary>
    string Name { get; }

    /// <summary>是否匹配该模型配置</summary>
    bool Matches(ModelConfig model);

    /// <summary>
    /// 选出真正用于投递的协议实例。默认返回自身。
    ///
    /// 需要「按模型名二次分派」的协议族（典型是百炼：同一个 base 下有同步/异步/兼容三套入口）
    /// 在这里返回具体的方案实例，从而保证 请求体、异步语义、轮询地址 三件事始终配套。
    /// </summary>
    IModelDeliveryProtocol SelectDeliveryProtocol(ModelConfig model) => this;

    /// <summary>
    /// 备选投递方案（不含自身，按优先级排序）。默认没有备选。
    ///
    /// 框架在首选方案被以 <c>404</c>（路径不存在）或「模型不存在」拒绝时，会依次尝试这些方案。
    /// 有了它，"地址填成了兼容入口 / 猜错了模型所属系列" 这类问题都能自动纠正，
    /// 而不是直接变成一条任务失败记录。
    /// </summary>
    IReadOnlyList<IModelDeliveryProtocol> GetFallbackProtocols(ModelConfig model) => [];

    /// <summary>
    /// 把模型配置里的 endpoint 解析成真正要请求的 URL。
    /// 返回空表示无法解析（应由调用方给出明确错误，而不是继续硬拼路径）。
    /// </summary>
    string? ResolveRequestUrl(ModelConfig model);

    /// <summary>
    /// 是否支持「图生图」（带参考图）。
    ///
    /// 不支持时框架会直接给出明确的失败原因——把参考图悄悄丢掉、照常文生图，
    /// 会产出一张和参考图毫无关系的图，用户根本无从判断是模型不行还是没接上。
    /// </summary>
    bool SupportsImageToImage => false;

    /// <summary>
    /// 按「是否带参考图」解析请求地址。默认忽略该标志。
    ///
    /// 需要覆写的典型场景：OpenAI 系的图生图走 <c>/images/edits</c> 而不是
    /// <c>/images/generations</c>，同一个 base 地址要落到不同路径上。
    /// </summary>
    string? ResolveRequestUrl(ModelConfig model, bool hasReferenceImages) => ResolveRequestUrl(model);

    /// <summary>
    /// 是否要以 multipart/form-data 提交（图生图在 OpenAI 系必须走文件字段）。
    /// 返回 <c>true</c> 时必须同时实现 <see cref="BuildMultipartContent" />。
    /// </summary>
    bool RequiresMultipart(ImageDeliveryRequest request) => false;

    /// <summary>构造 multipart 请求内容；<see cref="RequiresMultipart" /> 为 true 时调用</summary>
    MultipartFormDataContent? BuildMultipartContent(ImageDeliveryRequest request) => null;

    /// <summary>
    /// 极简 multipart（只含必需字段），用于首个请求被网关以「未知字段」拒绝后的降级重试。
    /// 返回 null 表示没有可降级的版本，框架会直接复用 <see cref="BuildMultipartContent" /> 的结果。
    /// </summary>
    MultipartFormDataContent? BuildMinimalMultipartContent(ImageDeliveryRequest request) => null;

    /// <summary>
    /// 是否走「提交任务 → 轮询任务结果」的异步流程。
    /// 目前只有 DashScope 原生接口（<c>X-DashScope-Async: enable</c>）属于这一类；
    /// 返回 <c>true</c> 时必须同时实现 <see cref="ResolveTaskStatusUrl"/>。
    /// </summary>
    bool IsAsynchronous => false;

    /// <summary>
    /// 提交请求时需要附加的请求头。默认无。
    /// DashScope 原生接口必须带 <c>X-DashScope-Async: enable</c>，否则报
    /// "current user api does not support synchronous calls"。
    /// </summary>
    IReadOnlyDictionary<string, string> BuildExtraHeaders(ImageDeliveryRequest request)
        => new Dictionary<string, string>();

    /// <summary>
    /// 构造请求体 JSON。默认按最通用的 OpenAI Images 约定；
    /// 结构特殊的协议（如 DashScope 原生）需要覆写。
    /// </summary>
    string BuildRequestBody(ImageDeliveryRequest request)
        => ImageDeliveryPayloads.OpenAiImages(request);

    /// <summary>
    /// 极简请求体（只含必需字段）。当首个请求被网关以「未知参数」拒绝时，
    /// 框架会用它重试一次——异构网关对可选字段的容忍度差异很大，
    /// 这层兜底能把"少一个字段就报 400"变成自动降级，而不是又一次报错。
    /// </summary>
    string BuildMinimalRequestBody(ImageDeliveryRequest request)
        => ImageDeliveryPayloads.OpenAiImagesMinimal(request);

    /// <summary>
    /// 异步协议专用：根据 <paramref name="taskId"/> 构造「查询任务结果」的地址；
    /// 同步协议返回 <c>null</c>。
    /// </summary>
    string? ResolveTaskStatusUrl(ModelConfig model, string taskId) => null;
}

/// <summary>命中的协议及其解析出的请求地址</summary>
public sealed record ProtocolResolution(IModelDeliveryProtocol Protocol, string Url);

/// <summary>
/// 协议解析器：按顺序挑选第一个匹配的协议。
/// 顺序很重要——更具体、更少歧义的协议要排在前面。
/// </summary>
public sealed class ModelDeliveryProtocolResolver
{
    private readonly IReadOnlyList<IModelDeliveryProtocol> _protocols;

    public ModelDeliveryProtocolResolver(IEnumerable<IModelDeliveryProtocol> protocols)
    {
        _protocols = protocols.ToList();
    }

    /// <summary>
    /// 解析出「命中的协议 + 请求地址」。
    /// 没有任何协议匹配、或匹配了却解析不出地址时返回 <c>null</c>（调用方报错，避免硬拼导致 404）。
    ///
    /// 返回的协议是<b>经二次分派后的具体方案</b>，调用方可以直接拿它构造请求体。
    /// </summary>
    /// <param name="model">模型配置</param>
    /// <param name="hasReferenceImages">
    /// 本次是否带参考图（图生图）。部分协议的入口路径会因此不同，
    /// 必须在解析阶段就带上，否则会拿着文生图的地址去发图生图请求。
    /// </param>
    public ProtocolResolution? Resolve(ModelConfig model, bool hasReferenceImages = false)
    {
        foreach (var candidate in _protocols)
        {
            if (!candidate.Matches(model))
                continue;

            var protocol = candidate.SelectDeliveryProtocol(model);
            var url = protocol.ResolveRequestUrl(model, hasReferenceImages);

            return string.IsNullOrWhiteSpace(url) ? null : new ProtocolResolution(protocol, url);
        }

        return null;
    }

    /// <summary>只取请求 URL（兼容既有调用方）</summary>
    public string? ResolveRequestUrl(ModelConfig model) => Resolve(model)?.Url;

    /// <summary>解析并附带命中的协议名，便于日志与排查</summary>
    public string? ResolveRequestUrl(ModelConfig model, out string? protocolName)
    {
        var resolution = Resolve(model);
        protocolName = resolution?.Protocol.Name;
        return resolution?.Url;
    }
}

/// <summary>
/// 内置协议的默认装配。抽成工厂是为了让 ModelService 与 ImageService 共用同一份
/// 协议清单与顺序，避免两处注册逐渐漂移。
/// </summary>
public static class ModelDeliveryProtocols
{
    /// <summary>
    /// 创建默认协议解析器。
    ///
    /// <b>顺序即优先级</b>：
    /// <list type="number">
    ///   <item>AliyunDashScopeProtocol —— 百炼（DashScope）协议族。命中后按模型名二次分派到
    ///         原生同步（<c>multimodal-generation/generation</c>，qwen-image）、
    ///         原生异步（<c>text2image/image-synthesis</c>，wan / wanx）、
    ///         或兼容入口（<c>/compatible-mode/v1/images/generations</c>），并把其余入口作为兜底方案。</item>
    ///   <item>AzureOpenAiProtocol —— 主机名精确命中，路径含 deployments 段</item>
    ///   <item>PassthroughProtocol —— 用户填了完整生成路径（最后一段是生成动作）</item>
    ///   <item>OpenAiImagesProtocol —— 兜底：其余按 OpenAI 兼容约定补 /images/generations</item>
    /// </list>
    ///
    /// 百炼必须排在 Passthrough 之前：<c>/api/v1/services/aigc/text2image/image-synthesis</c>
    /// 的最后一段含 "synthesis"，会被直通协议命中，从而绕过原生异步语义（漏掉轮询）。
    /// </summary>
    public static ModelDeliveryProtocolResolver CreateDefault() => new(
    [
        new AliyunDashScopeProtocol(),
        new AzureOpenAiProtocol(),
        new PassthroughProtocol(),
        new OpenAiImagesProtocol(),
    ]);
}
