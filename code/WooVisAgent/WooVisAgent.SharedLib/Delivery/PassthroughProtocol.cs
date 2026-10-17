using WooVisAgent.SharedLib.Domain;

namespace WooVisAgent.SharedLib.Delivery;

/// <summary>
/// 直通协议：当 endpoint 的<b>最后一段路径</b>看起来就是生成操作本身时，原样使用不做任何拼接。
///
/// 例如：
/// <list type="bullet">
///   <item><c>https://api.openai.com/v1/images/generations</c>（最后一段 generations）</item>
///   <item><c>https://gw.internal/v2/text2image</c>（最后一段 text2image）</item>
///   <item><c>https://my-gw.example.com/api/generate-image</c>（最后一段 generate-image）</item>
/// </list>
///
/// 注意：这里<b>不能</b>简单用"带路径段就直通"作为条件——那会把
/// <c>https://api.siliconflow.cn/v1</c> 这类"带版本路径的 base 地址"误判成完整路径，
/// 原样 POST 后返回 404。因此必须依赖"最后一段是否是操作动词"来判断。
/// </summary>
public sealed class PassthroughProtocol : IModelDeliveryProtocol
{
    public string Name => "passthrough";

    /// <summary>最后一段路径命中这些关键字，才认为用户填的是完整生成路径</summary>
    private static readonly string[] TerminalKeywords =
    [
        "generat",     // generation / generations / generate / generate-image
        "synthesis",   // image-synthesis
        "text2image",
        "t2i",
        "txt2img",
    ];

    public bool Matches(ModelConfig model)
    {
        var last = GetLastPathSegment(model.Endpoint);
        if (last.Length == 0)
            return false;

        return TerminalKeywords.Any(keyword => last.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    public string? ResolveRequestUrl(ModelConfig model) => ResolveRequestUrl(model, false);

    /// <summary>
    /// 直通协议默认原样使用用户填的地址。
    /// 唯一的例外是 OpenAI 风格的完整路径：<c>.../images/generations</c> 只收文生图，
    /// 带参考图时要把最后一段换成 <c>edits</c>，否则图生图请求会被打回。
    /// 其它自定义出口（如 <c>/v2/text2image</c>）没有可推导的编辑入口，保持原样。
    /// </summary>
    public string? ResolveRequestUrl(ModelConfig model, bool hasReferenceImages)
    {
        var endpoint = (model.Endpoint ?? string.Empty).Trim().TrimEnd('/');
        if (endpoint.Length == 0)
            return null;

        const string generationTail = "/images/generations";

        if (hasReferenceImages && endpoint.EndsWith(generationTail, StringComparison.OrdinalIgnoreCase))
            return $"{endpoint[..^"generations".Length]}edits";

        return endpoint;
    }

    /// <summary>
    /// 图生图一律按 OpenAI 的 <c>image</c> 文件字段提交。
    /// 直通协议命中的都是「用户自己填好的自定义网关」，
    /// multipart 文件字段是这类网关最普遍接受的带图方式。
    /// </summary>
    public bool SupportsImageToImage => true;

    public bool RequiresMultipart(ImageDeliveryRequest request) => request.HasReferenceImages;

    public MultipartFormDataContent? BuildMultipartContent(ImageDeliveryRequest request)
        => request.HasReferenceImages ? ImageDeliveryPayloads.OpenAiEditsForm(request) : null;

    public MultipartFormDataContent? BuildMinimalMultipartContent(ImageDeliveryRequest request)
        => request.HasReferenceImages ? ImageDeliveryPayloads.OpenAiEditsForm(request, minimal: true) : null;

    /// <summary>取 URL 的最后一段路径（不含查询串），无路径时返回空</summary>
    private static string GetLastPathSegment(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return string.Empty;

        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri))
            return string.Empty;

        var path = uri.AbsolutePath.Trim('/');
        if (path.Length == 0)
            return string.Empty;

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? string.Empty : segments[^1];
    }
}
