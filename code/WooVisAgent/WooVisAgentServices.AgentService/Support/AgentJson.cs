using System.Text;
using System.Text.Json;

namespace WooVisAgentServices.AgentService.Support;

/// <summary>
/// 模型返回内容的解析工具。
///
/// 背景：即使提示词里明确写了「只输出 JSON」，实测 qwen-plus 仍会出现
/// 代码块包裹、客套话、整段 JSON 被包成字符串、以及**长中文里夹着未转义字符导致整段 JSON 非法**。
/// 最后一种最坑：<c>JsonDocument.Parse</c> 一旦失败就什么都拿不到，
/// 润色会把整段 JSON 填进输入框，参数规划则全部为空。
///
/// 因此这里的策略是：不依赖整段 JSON 合法，直接按 <c>"key"</c> 定位字段逐个取值。
/// 合法 JSON 只是其中最顺利的一种情况。
/// </summary>
public static class AgentJson
{
    /// <summary>解析成 JSON 文档；失败返回 null（保留给需要结构化校验的场景）</summary>
    public static JsonDocument? TryParseObject(string? text)
    {
        var candidate = ExtractObject(text);
        if (candidate is null)
            return null;

        if (TryParse(candidate, out var document))
            return document;

        // 截出来的片段里仍带反斜杠转义：整体反转义后再试一次
        var unescaped = candidate.Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\r", string.Empty);
        return TryParse(unescaped, out document) ? document : null;
    }

    private static bool TryParse(string json, out JsonDocument? document)
    {
        try
        {
            document = JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            document = null;
            return false;
        }
    }

    /// <summary>从任意包裹里取出 JSON 对象文本；模型把 JSON 当字符串返回时先解一层</summary>
    private static string? ExtractObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var trimmed = text.Trim();

        if (trimmed.Length > 1 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            try
            {
                var unescaped = JsonSerializer.Deserialize<string>(trimmed);
                if (!string.IsNullOrWhiteSpace(unescaped))
                    return ExtractObject(unescaped);
            }
            catch (JsonException)
            {
                // 不是合法字符串字面量，退回按大括号截取
            }
        }

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;

        return trimmed[start..(end + 1)];
    }

    /// <summary>
    /// 逐层拆开「字段里又装了一份 JSON」。
    /// 实测会返回 <c>{"polished":"{\"polished\":\"...\"}"}</c> 这种形态。
    /// </summary>
    public static string Unwrap(string? text, string property, int maxDepth = 3)
    {
        var current = text?.Trim() ?? string.Empty;

        // 整体是字符串字面量："{\"a\":1}"
        if (current.Length > 1 && current[0] == '"' && current[^1] == '"')
        {
            try
            {
                var unescaped = JsonSerializer.Deserialize<string>(current);
                if (!string.IsNullOrWhiteSpace(unescaped))
                    current = unescaped.Trim();
            }
            catch (JsonException)
            {
                // 保持原样
            }
        }

        for (var depth = 0; depth < maxDepth; depth++)
        {
            var nested = ExtractString(current, property);
            if (string.IsNullOrWhiteSpace(nested) || !nested.TrimStart().StartsWith('{'))
                return current;

            if (ExtractString(nested, property) is null)
                return current;

            current = nested.Trim();
        }

        return current;
    }

    /// <summary>
    /// 取一个字符串字段。按 <c>"key"</c> 定位，容忍整段 JSON 非法、容忍单双引号外的各种包裹。
    /// </summary>
    public static string? ExtractString(string? text, string property)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        var keyIndex = text.IndexOf($"\"{property}\"", StringComparison.OrdinalIgnoreCase);
        if (keyIndex < 0)
            return null;

        var index = keyIndex + property.Length + 2;

        // 跳过冒号与空白
        while (index < text.Length && (text[index] == ':' || char.IsWhiteSpace(text[index])))
            index++;

        if (index >= text.Length || text[index] != '"')
            return null;

        index++;
        var builder = new StringBuilder();

        while (index < text.Length)
        {
            var current = text[index];

            if (current == '\\' && index + 1 < text.Length)
            {
                var next = text[index + 1];
                switch (next)
                {
                    case 'n': builder.Append('\n'); break;
                    case 'r': break;
                    case 't': builder.Append('\t'); break;
                    case 'u' when index + 5 < text.Length:
                        if (int.TryParse(text.AsSpan(index + 2, 4), System.Globalization.NumberStyles.HexNumber, null, out var code))
                            builder.Append((char)code);
                        index += 6;
                        continue;
                    default: builder.Append(next); break;
                }

                index += 2;
                continue;
            }

            // 未转义的换行意味着这段字符串其实没闭合，到此为止，避免把后面的结构一起吞进来
            if (current == '\n' || current == '\r')
                break;

            if (current == '"')
                break;

            builder.Append(current);
            index++;
        }

        var value = builder.ToString().Trim();
        return value.Length == 0 ? null : value;
    }

    /// <summary>取一个整数字段（模型有时会把数字写成字符串）</summary>
    public static int? ExtractInt(string? text, string property)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        var asString = ExtractString(text, property);
        if (int.TryParse(asString, out var parsed))
            return parsed;

        // 数字未加引号："durationSeconds":5
        var keyIndex = text.IndexOf($"\"{property}\"", StringComparison.OrdinalIgnoreCase);
        if (keyIndex < 0)
            return null;

        var index = keyIndex + property.Length + 2;
        while (index < text.Length && (text[index] == ':' || char.IsWhiteSpace(text[index])))
            index++;

        var start = index;
        while (index < text.Length && (char.IsDigit(text[index]) || text[index] == '-'))
            index++;

        return index > start && int.TryParse(text[start..index], out var number) ? number : null;
    }

    /// <summary>取一个字符串数组字段</summary>
    public static IReadOnlyList<string> ExtractStringArray(string? text, string property)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        var keyIndex = text.IndexOf($"\"{property}\"", StringComparison.OrdinalIgnoreCase);
        if (keyIndex < 0)
            return [];

        var bracket = text.IndexOf('[', keyIndex);
        if (bracket < 0)
            return [];

        var result = new List<string>();
        var index = bracket + 1;

        while (index < text.Length)
        {
            // 跳过空白与逗号
            while (index < text.Length && (char.IsWhiteSpace(text[index]) || text[index] == ','))
                index++;

            if (index >= text.Length || text[index] == ']')
                break;

            if (text[index] != '"')
                break;

            index++;
            var builder = new StringBuilder();
            while (index < text.Length && text[index] != '"')
            {
                if (text[index] == '\\' && index + 1 < text.Length)
                {
                    builder.Append(text[index + 1]);
                    index += 2;
                    continue;
                }

                builder.Append(text[index]);
                index++;
            }

            index++; // 跳过结束引号

            var value = builder.ToString().Trim();
            if (value.Length > 0)
                result.Add(value);
        }

        return result;
    }

    public static string? ReadString(this JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var item in element.EnumerateObject())
        {
            if (!string.Equals(item.Name, property, StringComparison.OrdinalIgnoreCase))
                continue;

            return item.Value.ValueKind switch
            {
                JsonValueKind.String => item.Value.GetString(),
                JsonValueKind.Number => item.Value.ToString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => item.Value.ToString(),
            };
        }

        return null;
    }

    public static int? ReadInt(this JsonElement element, string property)
    {
        var raw = element.ReadString(property);
        return int.TryParse(raw, out var value) ? value : null;
    }

    public static IReadOnlyList<string> ReadStringArray(this JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return [];

        foreach (var item in element.EnumerateObject())
        {
            if (!string.Equals(item.Name, property, StringComparison.OrdinalIgnoreCase))
                continue;

            if (item.Value.ValueKind == JsonValueKind.String)
            {
                var single = item.Value.GetString();
                return string.IsNullOrWhiteSpace(single) ? [] : [single];
            }

            if (item.Value.ValueKind != JsonValueKind.Array)
                return [];

            var list = new List<string>();
            foreach (var node in item.Value.EnumerateArray())
            {
                var text = node.ValueKind == JsonValueKind.String ? node.GetString() : node.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                    list.Add(text.Trim());
            }

            return list;
        }

        return [];
    }

    /// <summary>内层看起来「确实是想要的结构」才继续拆，避免误伤真实提示词</summary>
    public static JsonElement UnwrapStringified(
        this JsonElement root,
        string property,
        List<JsonDocument> keepAlive,
        Func<JsonElement, bool> accept,
        int maxDepth = 3)
    {
        for (var depth = 0; depth < maxDepth; depth++)
        {
            var nested = root.ReadString(property);
            if (string.IsNullOrWhiteSpace(nested) || !nested.TrimStart().StartsWith('{'))
                return root;

            var inner = TryParseObject(nested);
            if (inner is null)
                return root;

            keepAlive.Add(inner);

            if (!accept(inner.RootElement))
                return root;

            root = inner.RootElement;
        }

        return root;
    }
}
