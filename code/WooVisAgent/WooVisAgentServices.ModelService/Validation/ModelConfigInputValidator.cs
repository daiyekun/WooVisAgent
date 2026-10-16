using WooVisAgent.SharedLib.Domain;
using WooVisAgentServices.ModelService.Dtos;

namespace WooVisAgentServices.ModelService.Validation;

/// <summary>新增 / 编辑模型配置的入参校验</summary>
public static class ModelConfigInputValidator
{
    private static readonly string[] SupportedParamTypes = ["number", "string", "boolean", "select"];

    /// <summary>校验失败返回可展示的错误文案，通过则返回 null</summary>
    public static string? Validate(ModelConfigInput? input, out ModelCategory category)
    {
        category = ModelCategory.Image;

        if (input is null)
            return "请求内容不能为空。";

        if (string.IsNullOrWhiteSpace(input.Name))
            return "请填写模型名称。";

        if (input.Name.Trim().Length > 128)
            return "模型名称不能超过 128 个字符。";

        if (string.IsNullOrWhiteSpace(input.Provider))
            return "请选择或填写供应方。";

        if (input.Provider.Trim().Length > 64)
            return "供应方名称不能超过 64 个字符。";

        if (!ModelCategoryExtensions.TryParseWireValue(input.Category, out category))
            return "模型分类只能是 image、video 或 llm。";

        if (!string.IsNullOrWhiteSpace(input.Endpoint))
        {
            if (input.Endpoint.Trim().Length > 512)
                return "接口地址不能超过 512 个字符。";

            if (!Uri.TryCreate(input.Endpoint.Trim(), UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return "接口地址需以 http:// 或 https:// 开头。";
        }

        var parameters = input.Params ?? [];
        if (parameters.Count > 50)
            return "参数规格最多 50 条。";

        foreach (var spec in parameters)
        {
            if (string.IsNullOrWhiteSpace(spec.Key))
                return "参数的 key 不能为空。";

            if (string.IsNullOrWhiteSpace(spec.Label))
                return "参数的显示名称不能为空。";

            if (!SupportedParamTypes.Contains(spec.Type?.Trim().ToLowerInvariant()))
                return $"参数「{spec.Label}」的类型仅支持 number、string、boolean、select。";
        }

        return null;
    }

    public static string? NormalizeEndpoint(string? endpoint)
        => string.IsNullOrWhiteSpace(endpoint) ? null : endpoint.Trim().TrimEnd('/');

    public static string? NormalizeOptional(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }
}
