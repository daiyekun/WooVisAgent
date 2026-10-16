using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WooVisAgent.Api.Extensions;

/// <summary>
/// 让 64 位整数以字符串形式参与 JSON 序列化。
///
/// 背景：数据库主键统一使用雪花算法生成的 64 位整数（约 19 位），
/// 已超出 JavaScript 的安全整数范围（2^53-1），直接输出为 JSON number 会在前端丢精度、
/// 导致 ID 变形。前端各接口的 id 均为 string，因此契约上统一按字符串传输。
///
/// 反序列化同时兼容字符串与数字两种写法，避免历史报文失效。
/// </summary>
public sealed class LongToStringJsonConverter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String => long.TryParse(reader.GetString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : throw new JsonException("无法将字符串转换为 64 位整数。"),
            JsonTokenType.Number => reader.GetInt64(),
            JsonTokenType.Null => 0L,
            _ => throw new JsonException($"不支持从 {reader.TokenType} 读取 64 位整数。"),
        };

    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
}

/// <summary>可空 64 位整数同样以字符串输出</summary>
public sealed class NullableLongToStringJsonConverter : JsonConverter<long?>
{
    private static readonly LongToStringJsonConverter Inner = new();

    public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null ? null : Inner.Read(ref reader, typeToConvert, options);

    public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStringValue(value.Value.ToString(CultureInfo.InvariantCulture));
    }
}
