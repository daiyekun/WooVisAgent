using WooVisAgent.SharedLib.Domain;

namespace WooVisAgent.SharedLib.Delivery;

/// <summary>
/// 兜底协议：当用户直接填了一个「看起来就是完整生成路径」的 endpoint 时，
/// 不做任何拼接、原样使用。这是修复 404 的关键兜底——只要用户填对完整路径就一定不会 404。
///
/// 匹配条件放宽到「任何带路径段的绝对 URL」，但必须排在所有具体协议之后（作为 fallback）。
/// </summary>
public sealed class PassthroughProtocol : IModelDeliveryProtocol
{
    public string Name => "passthrough";

    public bool Matches(ModelConfig model)
    {
        var endpoint = (model.Endpoint ?? string.Empty).Trim();
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            return false;

        // 带明确路径段（不是纯域名根）才视为"用户填了完整路径"
        return !string.IsNullOrWhiteSpace(uri.AbsolutePath)
               && uri.AbsolutePath != "/";
    }

    public string? ResolveRequestUrl(ModelConfig model)
    {
        var endpoint = (model.Endpoint ?? string.Empty).Trim().TrimEnd('/');
        return endpoint.Length == 0 ? null : endpoint;
    }
}
