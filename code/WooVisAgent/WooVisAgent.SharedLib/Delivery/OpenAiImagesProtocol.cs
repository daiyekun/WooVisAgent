using WooVisAgent.SharedLib.Domain;

namespace WooVisAgent.SharedLib.Delivery;

/// <summary>
/// OpenAI Images 兼容协议，同时充当<b>默认兜底协议</b>。
///
/// 大多数图片模型（OpenAI、SiliconFlow、火山方舟、智谱、各类自建网关）都暴露
/// OpenAI 风格的 <c>/v1/images/generations</c>，因此把它作为"其他协议都不匹配时的默认约定"。
///
/// 解析规则（按顺序）：
/// <list type="number">
///   <item>endpoint 已以 <c>/images/generations</c> 结尾 → 原样使用（用户填了完整路径）</item>
///   <item>endpoint 无路径段（纯域名）→ 拼 <c>/v1/images/generations</c></item>
///   <item>endpoint 带路径（如 <c>/v1</c>、<c>/api/v3</c>）→ 在末尾拼 <c>/images/generations</c></item>
/// </list>
/// 第 3 条正是修复"填了带版本路径的 base 地址导致 404"的关键。
/// </summary>
public sealed class OpenAiImagesProtocol : IModelDeliveryProtocol
{
    public string Name => "openai-images";

    private const string GenerationSuffix = "/images/generations";

    /// <summary>作为默认兜底：只要是合法的 http(s) 绝对地址就匹配</summary>
    public bool Matches(ModelConfig model)
    {
        var endpoint = (model.Endpoint ?? string.Empty).Trim();
        return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    public string? ResolveRequestUrl(ModelConfig model)
    {
        var endpoint = (model.Endpoint ?? string.Empty).Trim().TrimEnd('/');
        if (endpoint.Length == 0)
            return null;

        // 1) 已经是完整生成路径
        if (endpoint.EndsWith(GenerationSuffix, StringComparison.OrdinalIgnoreCase))
            return endpoint;

        // 2) 纯域名（无路径）→ 补标准的 /v1/images/generations
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(uri.AbsolutePath)
            || uri.AbsolutePath == "/")
            return $"{endpoint}/v1{GenerationSuffix}";

        // 3) 带路径的 base（/v1、/api/v3 等）→ 末尾补 /images/generations
        return $"{endpoint}{GenerationSuffix}";
    }
}
