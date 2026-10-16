using System.Text.Encodings.Web;
using System.Text.Json;
using WooVisAgentServices.ModelService.Dtos;

namespace WooVisAgentServices.ModelService.Serialization;

/// <summary>
/// 模型参数规格与 ModelConfig.ParamsJson 之间的转换。
/// 统一使用 camelCase，与前端提交/读取的字段名保持一致。
/// </summary>
public static class ModelParamsSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(IEnumerable<ModelParamSpecModel>? specs)
        => JsonSerializer.Serialize(specs?.ToList() ?? [], Options);

    public static List<ModelParamSpecModel> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<ModelParamSpecModel>>(json, Options) ?? [];
        }
        catch (JsonException)
        {
            // 历史脏数据不应导致整个列表查询失败
            return [];
        }
    }
}
