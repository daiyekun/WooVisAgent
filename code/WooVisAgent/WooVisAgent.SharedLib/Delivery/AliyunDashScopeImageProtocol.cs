using WooVisAgent.SharedLib.Domain;

namespace WooVisAgent.SharedLib.Delivery;

/// <summary>
/// 阿里云百炼（Model Studio / DashScope）图片生成的三套真实入口。
///
/// 关键事实（依据百炼官方文档）：<b>compatible-mode 的 images/generations 在公共域名上并不存在</b>，
/// 直接请求它必然 404。图片生成必须落到下面这套原生入口上。
/// </summary>
public enum DashScopeImageMode
{
    /// <summary>
    /// OpenAI 兼容模式：<c>{base}/compatible-mode/v1/images/generations</c>（同步，尺寸用字母 x）。
    /// 仅业务空间专属域名（*.maas.aliyuncs.com）确认可用，公共域名会 404，故只作兜底。
    /// </summary>
    CompatibleOpenAi,

    /// <summary>
    /// 原生<b>同步</b>接口：<c>{base}/api/v1/services/aigc/multimodal-generation/generation</c>。
    /// qwen-image 系列（qwen-image-3.0-pro / qwen-image-2.1-turbo …）走这里，
    /// body 为 <c>input.messages</c> 结构，响应图片在 <c>output.choices[].message.content[].image</c>。
    /// </summary>
    SyncMultimodal,

    /// <summary>
    /// 原生<b>异步</b>接口：<c>{base}/api/v1/services/aigc/text2image/image-synthesis</c>。
    /// wan / wanx 系列（wan2.5-t2i-preview、wan2.6-t2i、wanx-v1 …）走这里，
    /// 必须带 <c>X-DashScope-Async: enable</c>，提交拿 <c>task_id</c> 后再轮询
    /// <c>{base}/api/v1/tasks/{task_id}</c>。
    /// </summary>
    AsyncText2Image,
}

/// <summary>
/// 百炼图片生成的单个投递方案（由 <see cref="AliyunDashScopeProtocol"/> 二次分派产生）。
///
/// 之所以做成"方案"而不是一个大协议，是因为同一家云的三套入口在
/// <b>路径</b>、<b>请求体结构</b>、<b>同步/异步</b>、<b>尺寸分隔符</b> 上全都不同，
/// 而具体该走哪套取决于用户填的<b>模型名</b>（qwen-image 还是 wan）。
/// 框架会在首选方案失败（404 / 模型不存在）时自动尝试下一套方案。
/// </summary>
public sealed class AliyunDashScopeImageProtocol : IModelDeliveryProtocol
{
    private const string CompatiblePath = "/compatible-mode/v1/images/generations";
    private const string CompatibleEditPath = "/compatible-mode/v1/images/edits";
    private const string SyncPath = "/api/v1/services/aigc/multimodal-generation/generation";
    private const string AsyncPath = "/api/v1/services/aigc/text2image/image-synthesis";

    public AliyunDashScopeImageProtocol(DashScopeImageMode mode) => Mode = mode;

    public DashScopeImageMode Mode { get; }

    public string Name => Mode switch
    {
        DashScopeImageMode.CompatibleOpenAi => "aliyun-dashscope-compatible",
        DashScopeImageMode.SyncMultimodal => "aliyun-dashscope-sync",
        _ => "aliyun-dashscope-async",
    };

    /// <summary>
    /// 本协议只由 <see cref="AliyunDashScopeProtocol"/> 按模型名显式选出，
    /// 不参与解析器的顺序匹配（否则会把别家的地址也抢过来）。
    /// </summary>
    public bool Matches(ModelConfig model) => false;

    public string? ResolveRequestUrl(ModelConfig model) => ResolveRequestUrl(model, false);

    /// <summary>
    /// 图生图时：兼容入口切到 <c>/compatible-mode/v1/images/edits</c>；
    /// 原生同步（qwen-image）仍是同一个地址，图片放在 <c>input.messages[].content</c> 里；
    /// 原生异步（wan / wanx 文生图）不支持图生图，由框架给出明确报错。
    /// </summary>
    public string? ResolveRequestUrl(ModelConfig model, bool hasReferenceImages)
    {
        if (!AliyunDashScopeEndpoints.TryResolveBase(model.Endpoint, out var baseUrl))
            return null;

        if (hasReferenceImages && Mode == DashScopeImageMode.CompatibleOpenAi)
            return $"{baseUrl}{CompatibleEditPath}";

        return Mode switch
        {
            DashScopeImageMode.CompatibleOpenAi => $"{baseUrl}{CompatiblePath}",
            DashScopeImageMode.SyncMultimodal => $"{baseUrl}{SyncPath}",
            _ => $"{baseUrl}{AsyncPath}",
        };
    }

    /// <summary>
    /// 只有<b>原生同步入口</b>（qwen-image 系列）真正支持图生图。
    ///
    /// 原生异步（wan / wanx 文生图）接口不接受参考图；
    /// OpenAI 兼容入口的 <c>/images/edits</c> 在百炼公共域名上同样不接受图生图 ——
    /// 公共域名会在收到 multipart 请求体之前直接断开连接，
    /// 客户端只会拿到一句无从下手的 "Error while copying content to a stream."。
    /// 与其让用户撞上这个错误，不如明确声明不支持，由框架给出可读的错误。
    /// </summary>
    public bool SupportsImageToImage => Mode == DashScopeImageMode.SyncMultimodal;

    /// <summary>
    /// 走原生同步入口的图生图用 JSON 里的 data URI 传图，不需要 multipart。
    /// 保留该实现是为了兼容确实存在 compatible-mode 业务空间域名的私有网关。
    /// </summary>
    public bool RequiresMultipart(ImageDeliveryRequest request)
        => Mode == DashScopeImageMode.CompatibleOpenAi && request.HasReferenceImages;

    public MultipartFormDataContent? BuildMultipartContent(ImageDeliveryRequest request)
        => RequiresMultipart(request) ? ImageDeliveryPayloads.OpenAiEditsForm(request) : null;

    public MultipartFormDataContent? BuildMinimalMultipartContent(ImageDeliveryRequest request)
        => RequiresMultipart(request) ? ImageDeliveryPayloads.OpenAiEditsForm(request, minimal: true) : null;

    public bool IsAsynchronous => Mode == DashScopeImageMode.AsyncText2Image;

    /// <summary>
    /// 本方案不可用时的其余入口（保持分派器给出的优先级顺序，只是把自己剔除掉）。
    ///
    /// 这一步很关键：解析器会把结果二次分派成具体方案，所以"兜底链条"必须挂在方案自己身上，
    /// 否则框架拿到方案后就再也看不到同一协议族的其他入口了。
    /// </summary>
    public IReadOnlyList<IModelDeliveryProtocol> GetFallbackProtocols(ModelConfig model)
        => [.. AliyunDashScopeProtocol.BuildPlans(model).Where(plan => plan.Mode != Mode)];

    /// <summary>只有原生异步入口需要 <c>X-DashScope-Async: enable</c>，缺了会报"不支持同步调用"</summary>
    public IReadOnlyDictionary<string, string> BuildExtraHeaders(ImageDeliveryRequest request)
        => IsAsynchronous
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-DashScope-Async"] = "enable" }
            : new Dictionary<string, string>();

    public string BuildRequestBody(ImageDeliveryRequest request) => Mode switch
    {
        // 兼容模式：参数平铺顶层，尺寸用字母 x，且没有 steps / cfg_scale
        DashScopeImageMode.CompatibleOpenAi => ImageDeliveryPayloads.OpenAiImages(
            request,
            size: request.OpenAiSize,
            includeDiffusionParameters: false),

        // 原生同步（qwen-image）：input.messages 结构，尺寸用星号
        DashScopeImageMode.SyncMultimodal => ImageDeliveryPayloads.DashScopeSync(request),

        // 原生异步（wan / wanx）：input.prompt 结构，尺寸用星号
        _ => ImageDeliveryPayloads.DashScopeAsync(request),
    };

    public string BuildMinimalRequestBody(ImageDeliveryRequest request) => Mode switch
    {
        DashScopeImageMode.CompatibleOpenAi
            => ImageDeliveryPayloads.OpenAiImagesMinimal(request, request.OpenAiSize),
        DashScopeImageMode.SyncMultimodal => ImageDeliveryPayloads.DashScopeSyncMinimal(request),
        _ => ImageDeliveryPayloads.DashScopeAsync(request),
    };

    /// <summary>只有原生异步入口才有任务查询地址：{base}/api/v1/tasks/{task_id}</summary>
    public string? ResolveTaskStatusUrl(ModelConfig model, string taskId)
    {
        if (!IsAsynchronous || string.IsNullOrWhiteSpace(taskId))
            return null;

        if (!AliyunDashScopeEndpoints.TryResolveBase(model.Endpoint, out var baseUrl))
            return null;

        return $"{baseUrl}/api/v1/tasks/{Uri.EscapeDataString(taskId)}";
    }
}
