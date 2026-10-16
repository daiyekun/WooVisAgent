using System.Text.Encodings.Web;
using System.Text.Json;
using WooVisAgent.SharedLib.Domain;

namespace WooVisAgentServices.ImageService.Serialization;

/// <summary>
/// 任务参数快照与素材列表的 JSON 转换。
/// 统一使用 camelCase，与前端读取的字段名保持一致（与 ModelParamsSerializer 同一套约定）。
/// </summary>
public static class GenerationTaskSerialization
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string SerializeAssets(IEnumerable<TaskAsset>? assets)
        => JsonSerializer.Serialize(assets?.ToList() ?? [], Options);

    public static List<TaskAsset> DeserializeAssets(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<TaskAsset>>(json, Options) ?? [];
        }
        catch (JsonException)
        {
            // 历史脏数据不应导致整个列表查询失败
            return [];
        }
    }

    public static string SerializeParams(IReadOnlyDictionary<string, object?>? parameters)
        => parameters is null || parameters.Count == 0
            ? "{}"
            : JsonSerializer.Serialize(parameters, Options);

    public static Dictionary<string, object?> DeserializeParams(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(json, Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>从参数快照里读一个整数，读不到或类型不符时返回默认值。</summary>
    public static int ReadInt(IReadOnlyDictionary<string, object?> parameters, string key, int fallback)
    {
        if (!parameters.TryGetValue(key, out var value))
            return fallback;

        return value switch
        {
            null => fallback,
            int intValue => intValue,
            long longValue => (int)longValue,
            double doubleValue => (int)doubleValue,
            JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var parsed) => parsed,
            JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var parsedLong) => (int)parsedLong,
            JsonElement element when element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out var parsedText) => parsedText,
            _ => fallback,
        };
    }

    /// <summary>从参数快照里读一个浮点数，读不到或类型不符时返回默认值。</summary>
    public static double ReadDouble(IReadOnlyDictionary<string, object?> parameters, string key, double fallback)
    {
        if (!parameters.TryGetValue(key, out var value))
            return fallback;

        return value switch
        {
            null => fallback,
            double doubleValue => doubleValue,
            float floatValue => floatValue,
            int intValue => intValue,
            long longValue => longValue,
            decimal decimalValue => (double)decimalValue,
            JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var parsed) => parsed,
            JsonElement element when element.ValueKind == JsonValueKind.String && double.TryParse(element.GetString(), out var parsedText) => parsedText,
            _ => fallback,
        };
    }

    /// <summary>从参数快照里读一个可空长整数（种子值），0 与缺失都视为"未指定"。</summary>
    public static long? ReadNullableLong(IReadOnlyDictionary<string, object?> parameters, string key)
    {
        if (!parameters.TryGetValue(key, out var value) || value is null)
            return null;

        var parsed = value switch
        {
            long longValue => longValue,
            int intValue => intValue,
            double doubleValue => (long)doubleValue,
            JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var parsedLong) => parsedLong,
            JsonElement element when element.ValueKind == JsonValueKind.String && long.TryParse(element.GetString(), out var parsedText) => parsedText,
            _ => (long?)null,
        };

        return parsed is > 0 ? parsed : null;
    }
}
