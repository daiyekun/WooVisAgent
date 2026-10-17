using System.Text.Json;
using System.Text.Json.Nodes;
using WooVisAgent.SharedLib.Domain;

namespace WooVisAgent.SharedLib.Delivery;

/// <summary>
/// 一次视频交付的「形状」：请求打到哪、请求体长什么样、结果怎么取回来。
///
/// 视频生成不像图片那样有 OpenAI Images 那样统一的行业标准——实测下来
/// 同为「异步生成」这件事，三家的差异贯穿在每一个环节：
/// <list type="bullet">
///   <item>提交路径：<c>/v1/videos</c>（OpenAI）、<c>/api/v1/services/aigc/video-generation/video-synthesis</c>（百炼）、
///         <c>/v1/video/submit</c>（SiliconFlow）；</item>
///   <item>任务号位置：顶层 <c>id</c> / <c>output.task_id</c> / <c>requestId</c>；</item>
///   <item>轮询方式：有的把 taskId 拼进路径（GET /v1/videos/{id}），
///         有的必须 POST 到固定地址并在 body 里传 id（SiliconFlow）；</item>
///   <item>产物形态：有的返回 URL（百炼 <c>output.video_url</c>），
///         有的根本不返回地址，要另外调 content 接口下二进制（OpenAI）。</item>
/// </list>
///
/// 如果为每家写一个协议类，那么「支持一个新厂商」就等于「改代码 + 发版」，
/// 而模型接入本来就是用户自己填 endpoint 就能做的事。这里把这些差异<b>全部变成配置数据</b>：
/// 内置几个预设开箱即用，未覆盖的厂商由用户在模型配置里写模板，不必改代码。
/// </summary>
public sealed class VideoDeliverySpec
{
    /// <summary>命中预设的名称，仅用于日志与排查（如 openai-videos / dashscope-video）</summary>
    public string PresetName { get; init; } = "custom";

    /// <summary>提交生成任务的路径。相对路径会拼在 endpoint 之后，支持 <c>{model}</c> 占位。</summary>
    public string SubmitPath { get; init; } = "/v1/videos";

    /// <summary>
    /// 查询任务状态的路径模板。<c>{taskId}</c> 会被替换成模型返回的任务号。
    /// 为空表示该预设是同步返回，不需要轮询。
    /// </summary>
    public string? StatusPath { get; init; }

    /// <summary>
    /// 查询任务状态用什么 HTTP 方法。
    /// 默认 GET（把 taskId 拼进 URL）；SiliconFlow 这类必须 POST 到固定地址并在 body 里传 id。
    /// </summary>
    public string StatusMethod { get; init; } = "GET";

    /// <summary>
    /// 当 <see cref="StatusMethod" /> 是 POST 时，任务号要放进请求体的哪个字段。
    /// 为空表示直接用 taskId 作为裸 body（部分网关接受裸字符串 id）。
    /// </summary>
    public string? StatusIdField { get; init; }

    /// <summary>从提交响应里取任务号的字段路径，支持 <c>output.task_id</c> 这样的点号路径</summary>
    public string TaskIdPath { get; init; } = "id";

    /// <summary>从查询响应里读任务状态的字段路径</summary>
    public string StatusValuePath { get; init; } = "status";

    /// <summary>判定「已完成」的状态值。比较时会忽略大小写。</summary>
    public string SucceededStatus { get; init; } = "completed";

    /// <summary>
    /// 判定「失败」的状态值（逗号分隔）。命中即把任务置为失败，
    /// 并把 <see cref="ErrorPath" /> 处的文案展示给用户。
    /// </summary>
    public string FailedStatuses { get; init; } = "failed,canceled,cancelled";

    /// <summary>从查询响应里读视频地址的字段路径，支持 <c>results.videos[0].url</c> 这样的数组下标</summary>
    public string? VideoUrlPath { get; init; }

    /// <summary>失败原因所在的字段路径，用于给用户一句可读的说明</summary>
    public string ErrorPath { get; init; } = "error.message";

    /// <summary>
    /// 产物是「下载地址」还是「二进制正文」。
    ///
    /// OpenAI 的 <c>/v1/videos/{id}/content</c> 直接返回视频字节、响应体里没有任何 URL，
    /// 按 URL 处理只会拿到一坨二进制当字符串解析，报出莫名其妙的错。
    /// </summary>
    public VideoOutputKind OutputKind { get; init; } = VideoOutputKind.Url;

    /// <summary>
    /// 当 <see cref="OutputKind" /> 是 <see cref="VideoOutputKind.ContentBinary" /> 时，
    /// 下载视频正文的路径模板（<c>{taskId}</c> 会被替换）。
    /// </summary>
    public string? ContentPath { get; init; }

    /// <summary>提交请求需要附加的请求头。百炼必须带 <c>X-DashScope-Async: enable</c>，否则直接报不支持同步调用。</summary>
    public IReadOnlyDictionary<string, string> ExtraHeaders { get; init; }
        = new Dictionary<string, string>();

    /// <summary>
    /// 请求体字段映射：内部语义名 → 请求体里的点号路径。
    ///
    /// 这是「通用」的关键。百炼要写 <c>input.prompt</c> / <c>input.img_url</c>，
    /// OpenAI 与 SiliconFlow 则是顶层 <c>prompt</c> / <c>image</c>。
    /// 差异只体现在这张映射表上，组装请求体的代码只有一份。
    /// </summary>
    public VideoRequestFieldMap RequestFields { get; init; } = VideoRequestFieldMap.Default;

    /// <summary>
    /// 分辨率的表达形式。百炼要 <c>720P</c>，OpenAI 要 <c>1280x720</c>，
    /// SiliconFlow 直接不传（按首帧图比例自动匹配）。
    /// </summary>
    public VideoResolutionStyle ResolutionStyle { get; init; } = VideoResolutionStyle.Omit;

    /// <summary>
    /// 该预设的路径是不是「厂商原生路径」——即从域名根算起，与 endpoint 自带的路径无关。
    ///
    /// 两类接口的定位不同，必须分开对待：
    /// <list type="bullet">
    ///   <item>百炼 / 硅基流动的视频接口挂在域名根下（<c>/api/v1/...</c>、<c>/v1/video/...</c>），
    ///         endpoint 只填域名即可，带任何前缀都会拼错；</item>
    ///   <item>OpenAI 预设本身就是兼容协议，<c>/v1/videos</c> 必须跟在 endpoint 后面——
    ///         endpoint 填 <c>https://gateway.example.com/proxy/v1</c> 时这个前缀是有意义的。</item>
    /// </list>
    /// 只有 <c>true</c> 时才会丢弃 endpoint 上的「兼容层」前缀，见 <see cref="Combine" />。
    /// </summary>
    public bool VendorNativePaths { get; init; } = true;

    /// <summary>解析出的提交 URL；解析不出来时返回 null，由调用方给出明确错误而不是硬拼路径。</summary>
    public string? ResolveSubmitUrl(ModelConfig model)
        => Combine(EndpointOf(model), SubmitPath, VendorNativePaths);

    /// <summary>解析轮询地址；模板里没有 <c>{taskId}</c> 且 <see cref="StatusPath" /> 为空时返回 null。</summary>
    public string? ResolveStatusUrl(ModelConfig model, string taskId)
        => Combine(EndpointOf(model), Replace(StatusPath, taskId), VendorNativePaths);

    /// <summary>解析视频正文的下载地址（仅 <see cref="VideoOutputKind.ContentBinary" /> 需要）</summary>
    public string? ResolveContentUrl(ModelConfig model, string taskId)
        => Combine(EndpointOf(model), Replace(ContentPath, taskId), VendorNativePaths);

    /// <summary>
    /// 把「接口根地址 + 相对路径」拼成最终 URL，并消除重复前缀。
    ///
    /// <para>
    /// 这一层不是可有可无的整洁性处理，而是<b>正确性前提</b>：用户的 endpoint 填法
    /// 与各家的相对路径习惯并不一致，两边很容易各带一段相同的版本前缀。
    /// 例如 endpoint 填 <c>https://dashscope.aliyuncs.com/api/v1</c>，
    /// 而预设路径是 <c>/api/v1/services/aigc/video-generation/video-synthesis</c>，
    /// 直接相加会得到 <c>.../api/v1/api/v1/services/...</c> —— 404，而且报错信息
    /// 指向"模型不存在"，排查方向会被彻底带偏。
    /// </para>
    ///
    /// <para>
    /// 规则：当路径部分<b>已经以 endpoint 的路径开头</b>时直接用 endpoint 的 origin 拼完整路径，
    /// 否则在 endpoint 后追加。于是下面几种填法都能得到正确地址：
    /// <c>https://api.openai.com</c>、<c>https://api.openai.com/v1</c>、
    /// <c>https://api.openai.com/v1/</c> 配 <c>/v1/videos</c>。
    /// </para>
    ///
    /// <para>
    /// 第二条规则处理的是反向的情况：endpoint 带的路径是<b>聊天用的 OpenAI 兼容层</b>
    /// （如百炼的 <c>/compatible-mode/v1</c>），而视频接口在厂商那里根本没有兼容层，
    /// 一律走域名根。用户从聊天模型整段复制地址过来是最常见的操作，
    /// 于是拼出 <c>/compatible-mode/v1/api/v1/services/...</c> 这种地址，
    /// 服务端只回一个 404——且错误信息完全不提"前缀拼错了"，排查方向会被带偏。
    /// 因此当预设声明走原生路径时，这类前缀直接丢弃、回到 origin 重拼。
    /// </para>
    /// </summary>
    private static string? Combine(string endpoint, string? path, bool vendorNativePaths)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return null;

        if (string.IsNullOrWhiteSpace(path))
            return TrimTrailingSlash(endpoint);

        var baseUrl = TrimTrailingSlash(endpoint);
        var trimmedPath = path.StartsWith('/') ? path : "/" + path;

        var origin = OriginOf(baseUrl);
        var endpointPath = PathOf(baseUrl);

        // endpoint 自带的路径段已经是完整路径的前缀 → 用 origin 重新拼，避免重复
        if (endpointPath.Length > 0 && trimmedPath.StartsWith(endpointPath, StringComparison.OrdinalIgnoreCase))
            return origin + trimmedPath;

        // endpoint 只剩一个与视频无关的兼容层前缀 → 同样回到 origin
        if (vendorNativePaths && endpointPath.Length > 0 && IsCompatibilityShim(endpointPath))
            return origin + trimmedPath;

        return baseUrl + trimmedPath;
    }

    /// <summary>
    /// 已知的「OpenAI 兼容层」路径前缀。
    ///
    /// 这些前缀只服务于 <c>/v1/chat/completions</c> 一类的文本接口，
    /// 视频生成各家都只有原生入口，因此带上它们必然 404。
    /// </summary>
    private static readonly string[] CompatibilityShims =
    [
        "/compatible-mode/v1",
        "/compatible-mode",
    ];

    private static bool IsCompatibilityShim(string endpointPath)
    {
        foreach (var shim in CompatibilityShims)
        {
            if (endpointPath.Equals(shim, StringComparison.OrdinalIgnoreCase)
                || endpointPath.EndsWith(shim, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string Replace(string? template, string taskId)
        => string.IsNullOrWhiteSpace(template)
            ? string.Empty
            : template.Replace("{taskId}", taskId, StringComparison.Ordinal);

    private static string EndpointOf(ModelConfig model) => model.Endpoint ?? string.Empty;

    /// <summary>取出 scheme://host[:port] 部分</summary>
    private static string OriginOf(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority)
            : url;

    /// <summary>取出 path 部分（含前导斜杠，没有则为空串）</summary>
    private static string PathOf(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.AbsolutePath.TrimEnd('/')
            : string.Empty;

    private static string TrimTrailingSlash(string value)
        => value.EndsWith('/') ? value[..^1] : value;
}

/// <summary>视频产物的取回方式</summary>
public enum VideoOutputKind
{
    /// <summary>响应体里带一个 URL，按该 URL 下载</summary>
    Url = 0,

    /// <summary>响应体不带 URL，需要额外请求 content 接口拿二进制（如 OpenAI）</summary>
    ContentBinary = 1,

    /// <summary>响应体里直接是 base64 编码的视频数据</summary>
    Base64 = 2,
}

/// <summary>分辨率字段的表达形式</summary>
public enum VideoResolutionStyle
{
    /// <summary>不传分辨率，由模型自行决定（如 SiliconFlow 按首帧图比例匹配）</summary>
    Omit = 0,

    /// <summary>传 <c>1280x720</c> 形式的像素尺寸（OpenAI）</summary>
    PixelSize = 1,

    /// <summary>传 <c>720P</c> 形式的档位（百炼）</summary>
    Tier = 2,

    /// <summary>额外传宽高比 <c>16:9</c>，与档位配合使用（百炼）</summary>
    TierAndRatio = 3,
}

/// <summary>
/// 请求体字段映射：把内部语义映射到各家不同的字段路径。
///
/// 之所以不把「请求体」整段做成模板，是因为各家除了字段名不同，
/// 还有嵌套层级不同（百炼把提示词包在<c>input</c> 里、参数包在 <c>parameters</c> 里）。
/// 拆成「字段 → 路径」之后，组装逻辑可以统一按路径写入 JsonObject。
///
/// <para>
/// 所有字段都可为 <c>null</c>，含义是<b>「该预设不传这个字段」</b>——
/// 而不是"用默认值"。这点很关键：多数视频接口对未知字段直接报 400，
/// 而负向提示词、帧率、时长恰恰不是每个接口都支持，
/// 因此"没配就干脆不传"必须是一等公民，而不是退化成某个默认字段名。
/// </para>
/// </summary>
public sealed class VideoRequestFieldMap
{
    public string? Model { get; init; } = "model";

    public string? Prompt { get; init; } = "prompt";

    public string? NegativePrompt { get; init; } = "negative_prompt";

    /// <summary>图生视频的首帧图字段。注意百炼是 <c>input.img_url</c>，不是 <c>image_url</c>。</summary>
    public string? FirstFrame { get; init; } = "image";

    public string? Duration { get; init; } = "seconds";

    public string? Resolution { get; init; } = "size";

    public string? Ratio { get; init; }

    /// <summary>
    /// 帧率字段路径。为空表示该预设不支持传帧率——大多数视频接口不接受自定义 fps，
    /// 多传一个它不认识的字段，轻则 400、重则被静默忽略。
    /// </summary>
    public string? Fps { get; init; }

    public string? Seed { get; init; } = "seed";

    /// <summary>
    /// 模型扩展参数要挂到哪个对象下。
    /// 百炼是 <c>parameters</c>，OpenAI 是顶层，SiliconFlow 是顶层。
    /// 空字符串表示挂到请求体根。
    /// </summary>
    public string ExtraParamsContainer { get; init; } = "";

    /// <summary>百炼这类协议的默认映射</summary>
    public static VideoRequestFieldMap Default => new();
}

/// <summary>一次视频生成请求的中立描述，交给协议层去组装成各家形状</summary>
public sealed record VideoDeliveryRequest(
    ModelConfig Model,
    string Prompt,
    string? NegativePrompt,
    /// <summary>首帧图（已转成公网可访问的 data URI 或 URL），图生视频时非空</summary>
    string? FirstFrameUrl,
    /// <summary>分辨率，原始形态（如 1280x720 或 720P），由协议层按 <see cref="VideoResolutionStyle" /> 转换</summary>
    string? Resolution,
    /// <summary>时长（秒）。为 null 表示用模型默认值。</summary>
    int? DurationSeconds,
    /// <summary>帧率。部分模型不支持，为 null 时不传。</summary>
    int? Fps,
    long? Seed,
    /// <summary>模型扩展参数，键为 ModelParamSpec.key</summary>
    IReadOnlyDictionary<string, object?> ExtraParams)
{
    /// <summary>是否是图生视频（带了首帧图）</summary>
    public bool IsImageToVideo => !string.IsNullOrWhiteSpace(FirstFrameUrl);

    /// <summary>实际使用的种子；未指定时给一个稳定值，避免每次都随机导致复现困难</summary>
    public long EffectiveSeed => Seed ?? Random.Shared.NextInt64(1, int.MaxValue);
}

/// <summary>按点号路径读写 JsonNode 的小工具，同时支持 <c>a.b[0].c</c> 形式的数组下标</summary>
public static class JsonNodePath
{
    /// <summary>
    /// 沿路径写入值。路径形如 <c>input.prompt</c>、<c>parameters.resolution</c>。
    ///
    /// 中途不存在的层级会自动补成 JsonObject，这样 <c>input.prompt</c> 与
    /// <c>input.negative_prompt</c> 连续写两次不会互相覆盖掉父节点。
    /// 路径为空时不做任何事（用于「该预设不传这个字段」的表达）。
    /// </summary>
    public static void Set(JsonObject root, string? path, JsonNode? value)
    {
        if (root is null || string.IsNullOrWhiteSpace(path) || value is null)
            return;

        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return;

        var current = root;

        for (var i = 0; i < segments.Length - 1; i++)
        {
            var name = segments[i];

            if (current[name] is not JsonObject next)
            {
                next = new JsonObject();
                current[name] = next;
            }

            current = next;
        }

        current[segments[^1]] = value;
    }

    /// <summary>
    /// 沿路径读值，返回 null 表示路径不存在或值为空。
    /// 支持 <c>[n]</c> 数组下标：<c>results.videos[0].url</c>。
    /// </summary>
    public static JsonNode? Get(JsonNode? root, string? path)
    {
        if (root is null || string.IsNullOrWhiteSpace(path))
            return null;

        var current = root;

        foreach (var rawSegment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current is null)
                return null;

            // 把 "videos[0]" 拆成 "videos" 与下标 0
            var bracket = rawSegment.IndexOf('[', StringComparison.Ordinal);
            var name = bracket >= 0 ? rawSegment[..bracket] : rawSegment;
            var indexText = bracket >= 0 ? rawSegment[(bracket + 1)..].TrimEnd(']') : null;

            if (name.Length > 0)
            {
                if (current is JsonObject obj)
                {
                    if (!obj.TryGetPropertyValue(name, out current))
                        return null;
                }
                else
                {
                    return null;
                }
            }

            if (indexText is not null)
            {
                if (!int.TryParse(indexText, out var index))
                    return null;

                if (current is JsonArray array)
                {
                    if (index < 0 || index >= array.Count)
                        return null;

                    current = array[index];
                }
                else
                {
                    return null;
                }
            }
        }

        return current;
    }

    /// <summary>按路径读字符串；读不到或不是字符串返回 null</summary>
    public static string? GetString(JsonNode? root, string? path)
        => Get(root, path)?.GetValue<string>();

    /// <summary>
    /// 按路径读一个可能不是字符串的标量并转成字符串。
    /// 状态字段有时是字符串（<c>"SUCCEEDED"</c>）有时是数字码，因此不能强转 string。
    /// </summary>
    public static string? GetScalar(JsonNode? root, string? path)
    {
        var node = Get(root, path);
        if (node is null)
            return null;

        return node switch
        {
            JsonValue value => value.ToString(),
            _ => node.ToJsonString(),
        };
    }

    /// <summary>把任意 JSON 文本解析成 <see cref="JsonNode" />，解析失败返回 null</summary>
    public static JsonNode? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            // 非 JSON 正文（网关返回的 HTML 错误页等）不是这里该处理的问题，
            // 交给上层按状态码给出提示。
            return null;
        }
    }
}