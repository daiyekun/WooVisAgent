using System.Text.Encodings.Web;
using System.Text.Json;
using WooVisAgent.SharedLib.Domain;

namespace WooVisAgentServices.VideoService.Serialization;

/// <summary>
/// 任务参数快照与素材列表的 JSON 转换。
///
/// 这里的约定必须与 <c>ImageService.Serialization.GenerationTaskSerialization</c>
/// 完全一致（camelCase + 宽松取值）——两张表由同一套
/// <c>GenerationTask</c> 映射，<c>ParamsJson</c> / <c>InputsJson</c> 是共用的列，
/// 用两套不同的序列化规则去读写同一列，早晚会读出互相看不懂的数据。
/// </summary>
public static class VideoGenerationSerialization
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

    public static int? ReadNullableInt(IReadOnlyDictionary<string, object?> parameters, string key)
    {
        var value = ReadInt(parameters, key, 0);
        return value > 0 ? value : null;
    }

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

    public static bool ReadBool(IReadOnlyDictionary<string, object?> parameters, string key, bool fallback)
    {
        if (!parameters.TryGetValue(key, out var value) || value is null)
            return fallback;

        return value switch
        {
            bool flag => flag,
            int intValue => intValue != 0,
            long longValue => longValue != 0,
            JsonElement element when element.ValueKind is JsonValueKind.True or JsonValueKind.False => element.GetBoolean(),
            JsonElement element when element.ValueKind == JsonValueKind.Number => element.TryGetInt32(out var number) && number != 0,
            JsonElement element when element.ValueKind == JsonValueKind.String && bool.TryParse(element.GetString(), out var parsed) => parsed,
            _ => fallback,
        };
    }

    public static string? ReadString(IReadOnlyDictionary<string, object?> parameters, string key)
    {
        if (!parameters.TryGetValue(key, out var value) || value is null)
            return null;

        var text = value switch
        {
            string str => str,
            JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString(),
            _ => value.ToString(),
        };

        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    /// <summary>从参数快照里读一个可空长整数（种子值），0 与缺失都视为"未指定"。</summary>
    public static long? ReadNullableLong(IReadOnlyDictionary<string, object?> parameters, string key)
    {
        if (!parameters.TryGetValue(key, out var value) || value is null)
            return null;

        var parsed = value switch
        {
            long longValue => longValue,
            int intValue => (long)intValue,
            double doubleValue => (long)doubleValue,
            JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var parsedLong) => parsedLong,
            JsonElement element when element.ValueKind == JsonValueKind.String && long.TryParse(element.GetString(), out var parsedText) => parsedText,
            _ => (long?)null,
        };

        return parsed is > 0 ? parsed : null;
    }

    /// <summary>
    /// 取出模型扩展参数（除掉视频自身的固定字段）。
    ///
    /// 这些键会原样拼进模型请求体，因此必须先剔除
    /// resolution / durationSeconds / fps / seed 这些已经被协议层单独处理过的键，
    /// 否则同一个值会以两个不同字段名各发一遍。
    /// </summary>
    public static IReadOnlyDictionary<string, object?> ExtractExtraParams(
        IReadOnlyDictionary<string, object?> parameters)
    {
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "resolution",
            "durationSeconds",
            "fps",
            "withAudio",
            "seed",
            "firstFrameUrl",
            "lastFrameUrl",
        };

        var extra = new Dictionary<string, object?>();

        foreach (var (key, value) in parameters)
        {
            if (string.IsNullOrWhiteSpace(key) || reserved.Contains(key))
                continue;

            extra[key] = value;
        }

        return extra;
    }
}