using System.Text.Encodings.Web;
using System.Text.Json;
using WooVisAgent.SharedLib.Domain;

namespace WooVisAgent.EventBus.Events;

/// <summary>
/// <see cref="VideoGenerationRequested" /> 的构造入口。
///
/// 放在 EventBus 而不是某个服务里，是因为**视频消息有两个构造点**：
/// <list type="bullet">
///   <item>提交视频任务（Api → VideoService 的用例）；</item>
///   <item>重试视频任务（ImageService 的 <c>RetryGenerationTask</c>，该用例是类型无关的，
///         图片视频共用，因此视频重试也走它）。</item>
/// </list>
/// 两处各写一份字段映射，迟早会漂移——改了一处忘了另一处，
/// 表现就是「重试出来的任务丢了首帧图」这种极难排查的问题。
/// 因此统一收敛到这里，谁发消息都调这一个方法。
/// </summary>
public static class VideoGenerationRequestedFactory
{
    /// <summary>
    /// 从已落库的任务构造消息。
    ///
    /// 所有生成参数都取自任务的参数快照（<c>ParamsJson</c>）与输入素材（<c>InputsJson</c>），
    /// 不读数据库里的模型配置，因此消息可以安全重放。
    /// </summary>
    public static VideoGenerationRequested Create(
        GenerationTask task,
        Dictionary<string, string>? traceHeaders = null,
        string? messageId = null)
    {
        var parameters = DeserializeParameters(task.ParamsJson);
        var inputs = DeserializeAssets(task.InputsJson);

        // 首帧图取第一张输入素材。图生视频只支持单张首帧，
        // 前端表单也只有一个上传位，所以这里不存在"多张取哪张"的歧义。
        var firstFrame = inputs.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Url));

        return new VideoGenerationRequested(
            task.Id,
            task.OwnerId,
            task.Type,
            task.ModelConfigId,
            task.Prompt,
            task.NegativePrompt,
            task.Name,
            firstFrame,
            ReadString(parameters, "resolution"),
            ReadNullableInt(parameters, "durationSeconds"),
            ReadNullableInt(parameters, "fps"),
            ReadBool(parameters, "withAudio", false),
            ReadNullableLong(parameters, "seed"),
            task.ParamsJson,
            DateTimeOffset.UtcNow,
            MessageId: messageId ?? Guid.NewGuid().ToString("N"),
            TraceHeaders: traceHeaders);
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static Dictionary<string, object?> DeserializeParameters(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(json, Options) ?? [];
        }
        catch (JsonException)
        {
            // 历史脏数据不该让整条消息构造失败，交给下游按默认值继续
            return [];
        }
    }

    private static List<TaskAsset> DeserializeAssets(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<TaskAsset>>(json, Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?> parameters, string key)
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

    private static int? ReadNullableInt(IReadOnlyDictionary<string, object?> parameters, string key)
    {
        var value = ReadInt(parameters, key, 0);
        return value > 0 ? value : null;
    }

    private static int ReadInt(IReadOnlyDictionary<string, object?> parameters, string key, int fallback)
    {
        if (!parameters.TryGetValue(key, out var value))
            return fallback;

        return value switch
        {
            int intValue => intValue,
            long longValue => (int)longValue,
            double doubleValue => (int)doubleValue,
            JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var parsed) => parsed,
            JsonElement element when element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out var parsedText) => parsedText,
            _ => fallback,
        };
    }

    private static bool ReadBool(IReadOnlyDictionary<string, object?> parameters, string key, bool fallback)
    {
        if (!parameters.TryGetValue(key, out var value) || value is null)
            return fallback;

        return value switch
        {
            bool flag => flag,
            int intValue => intValue != 0,
            JsonElement element when element.ValueKind is JsonValueKind.True or JsonValueKind.False => element.GetBoolean(),
            JsonElement element when element.ValueKind == JsonValueKind.String && bool.TryParse(element.GetString(), out var parsed) => parsed,
            _ => fallback,
        };
    }

    private static long? ReadNullableLong(IReadOnlyDictionary<string, object?> parameters, string key)
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
}