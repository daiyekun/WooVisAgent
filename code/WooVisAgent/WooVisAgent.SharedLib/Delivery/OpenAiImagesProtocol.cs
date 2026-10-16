using WooVisAgent.SharedLib.Domain;

namespace WooVisAgent.SharedLib.Delivery;

/// <summary>
/// OpenAI Images 兼容协议（<c>/v1/images/generations</c>）。
///
/// 适配两种情况：
/// <list type="bullet">
///   <item>用户填的是 base 地址（如 https://api.openai.com 或 https://xxx.openai.azure.com）→ 拼 /v1/images/generations</item>
///   <item>用户直接填了完整路径（以 /images/generations 结尾）→ 原样使用</item>
/// </list>
/// </summary>
public sealed class OpenAiImagesProtocol : IModelDeliveryProtocol
{
    public string Name => "openai-images";

    public bool Matches(ModelConfig model)
    {
        var provider = model.Provider?.Trim() ?? string.Empty;
        if (provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
            return true;

        // 用户自定义供应方但 endpoint 形如 /v1 或 /images/generations 结尾，也按 OpenAI 兼容处理
        return EndpointLooksOpenAiCompatible(model.Endpoint);
    }

    public string? ResolveRequestUrl(ModelConfig model)
    {
        var endpoint = (model.Endpoint ?? string.Empty).Trim().TrimEnd('/');
        if (endpoint.Length == 0)
            return null;

        if (endpoint.EndsWith("/images/generations", StringComparison.OrdinalIgnoreCase))
            return endpoint;

        if (endpoint.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            return $"{endpoint}/images/generations";

        return $"{endpoint}/v1/images/generations";
    }

    private static bool EndpointLooksOpenAiCompatible(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return false;

        var trimmed = endpoint.Trim();
        return trimmed.EndsWith("/images/generations", StringComparison.OrdinalIgnoreCase)
               || trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase);
    }
}
