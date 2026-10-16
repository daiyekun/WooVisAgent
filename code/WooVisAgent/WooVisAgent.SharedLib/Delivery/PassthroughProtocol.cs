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

    public string? ResolveRequestUrl(ModelConfig model)
    {
        var endpoint = (model.Endpoint ?? string.Empty).Trim().TrimEnd('/');
        return endpoint.Length == 0 ? null : endpoint;
    }

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
