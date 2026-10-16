using WooVisAgent.SharedLib.Domain;

namespace WooVisAgent.SharedLib.Delivery;

/// <summary>
/// Azure OpenAI 协议。
///
/// Azure 的图片生成路径与 OpenAI 不同，形如：
/// <c>https://{resource}.openai.azure.com/openai/deployments/{deployment}/images/generations?api-version=2024-02-01</c>
/// 其中 deployment 通常对应 ModelKey，api-version 是必填查询参数。
///
/// 如果只按默认 OpenAI 兜底拼 <c>/v1/images/generations</c>，Azure 端点会返回 404。
/// </summary>
public sealed class AzureOpenAiProtocol : IModelDeliveryProtocol
{
    public string Name => "azure-openai";

    private const string DefaultApiVersion = "2024-02-01";

    public bool Matches(ModelConfig model)
    {
        if (!Uri.TryCreate((model.Endpoint ?? string.Empty).Trim(), UriKind.Absolute, out var uri))
            return false;

        return uri.Host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase);
    }

    public string? ResolveRequestUrl(ModelConfig model)
    {
        var endpoint = (model.Endpoint ?? string.Empty).Trim().TrimEnd('/');
        if (endpoint.Length == 0 || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            return null;

        var deployment = string.IsNullOrWhiteSpace(model.ModelKey) ? "gpt-image-1" : model.ModelKey!.Trim();

        // 用户已填了完整 deployments 路径 → 原样使用（也许还带了自己的 api-version）
        if (uri.AbsolutePath.Contains("/deployments/", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.Contains("generations", StringComparison.OrdinalIgnoreCase))
            return endpoint;

        var origin = $"{uri.Scheme}://{uri.Authority}";
        return $"{origin}/openai/deployments/{deployment}/images/generations?api-version={DefaultApiVersion}";
    }
}
