using System.Text.Encodings.Web;
using System.Text.Json;

namespace WooVisAgent.Services.Common.Helper;

public static class JsonHelper
{
    private static readonly JsonSerializerOptions DefaultOptions = new()
    {
        // 正式环境使用 WriteIndented = false 压缩 JSON，节省 Token
        WriteIndented = true,
        // 不转义中文字符串，避免编码问题
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string ToJson(this object obj)
    {
        return JsonSerializer.Serialize(obj, DefaultOptions);
    }
}
