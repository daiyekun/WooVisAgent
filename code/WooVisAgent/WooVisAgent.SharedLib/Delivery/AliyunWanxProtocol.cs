using WooVisAgent.SharedLib.Domain;

namespace WooVisAgent.SharedLib.Delivery;

/// <summary>
/// 阿里云百炼（通义万相）协议。
///
/// 通义万相采用 OpenAI 兼容的路径风格，但落在 <c>/api/v1/services/aigc/text2image/image-synthesis</c>。
/// 这里统一约定：endpoint 填到 <c>/api/v1</c> 或带完整路径均可。
/// </summary>
public sealed class AliyunWanxProtocol : IModelDeliveryProtocol
{
    public string Name => "aliyun-wanx";

    private const string SynthesisPath = "/services/aigc/text2image/image-synthesis";

    public bool Matches(ModelConfig model)
        => string.Equals(model.Provider?.Trim(), "AliyunBailian", StringComparison.OrdinalIgnoreCase)
           || string.Equals(model.Provider?.Trim(), "通义万相", StringComparison.OrdinalIgnoreCase);

    public string? ResolveRequestUrl(ModelConfig model)
    {
        var endpoint = (model.Endpoint ?? string.Empty).Trim().TrimEnd('/');
        if (endpoint.Length == 0)
            return null;

        if (endpoint.EndsWith(SynthesisPath, StringComparison.OrdinalIgnoreCase))
            return endpoint;

        if (endpoint.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
            return $"{endpoint}{SynthesisPath}";

        return $"{endpoint}/api/v1{SynthesisPath}";
    }
}
