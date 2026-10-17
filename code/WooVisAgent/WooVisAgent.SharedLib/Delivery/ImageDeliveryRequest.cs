using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WooVisAgent.SharedLib.Delivery;

/// <summary>
/// 一次「图片交付」请求的中立描述。
///
/// 协议层用它构造各自风格的请求体——OpenAI 系把 prompt 放在顶层，
/// DashScope 原生接口则要包成 <c>input.prompt</c> + <c>parameters</c>。
/// 把它放在 SharedLib（而不是复用 ImageService 的 DTO）是为了让协议实现
/// 不反向依赖上层服务，从而保持 SharedLib 处于依赖图最底层。
/// </summary>
/// <summary>
/// 一张已解析成字节的参考图（图生图的输入）。
///
/// 之所以要求协议层拿到<b>字节</b>而不只是 URL：参考图是用户上传到本站
/// <c>/uploads</c> 下的文件，模型服务在公网访问不到这个地址，
/// 因此必须就地读出来，再按协议需要转成 multipart 文件字段或 data URI。
/// </summary>
public sealed record ImageDeliveryReference(string Url, byte[] Bytes, string ContentType, string FileName)
{
    /// <summary>DashScope 等走 JSON 的协议用 data URI 携带图片</summary>
    public string DataUri => $"data:{ContentType};base64,{Convert.ToBase64String(Bytes)}";
}

public sealed record ImageDeliveryRequest(
    string? ModelKey,
    string Prompt,
    string? NegativePrompt,
    int Width,
    int Height,
    int Steps,
    double CfgScale,
    long? Seed,
    int Index,
    IReadOnlyList<ImageDeliveryReference>? ReferenceImages = null)
{
    /// <summary>协议层允许携带的参考图上限，与提交用例的校验保持一致</summary>
    public const int MaxReferenceImages = 8;

    /// <summary>OpenAI 系的尺寸写法：<c>1024x1024</c></summary>
    public string OpenAiSize => $"{Width}x{Height}";

    /// <summary>DashScope（百炼）的尺寸写法：<c>1024*1024</c>（星号，不是字母 x）</summary>
    public string DashScopeSize => $"{Width}*{Height}";

    /// <summary>同批次内多张图按 seed 递推，避免一张重复生成多份</summary>
    public long? EffectiveSeed => Seed is { } seed ? seed + Index : null;

    /// <summary>参考图（已解析成字节，可能少于提交数量——取不到的会被丢弃）</summary>
    public IReadOnlyList<ImageDeliveryReference> References => ReferenceImages ?? [];

    /// <summary>是否为「图生图」。协议层据此切换入口与请求体结构</summary>
    public bool HasReferenceImages => References.Count > 0;
}

/// <summary>协议层构造请求体时使用的 JSON 约定（与任务参数快照保持同一套 camelCase 规则）</summary>
public static class ModelDeliveryJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // 中文提示词不要被转义成 \uXXXX：一是请求体更小，
        // 二是抓包 / 记日志时能直接看懂到底发了什么内容。
        // 这里只用于 HTTP 请求体，不涉及 HTML 输出，因此不受 XSS 场景影响。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>
/// 各协议的请求体构造器。
///
/// 之所以把「请求体」也交给协议层，是因为「路径不同」和「body 结构不同」是同一个问题的两面：
/// 通义万相原生接口不仅路径是 <c>/services/aigc/text2image/image-synthesis</c>，
/// 连 body 都要求 <c>input.prompt</c>，只改路径一样会失败。
/// </summary>
public static class ImageDeliveryPayloads
{
    /// <summary>
    /// OpenAI Images 风格的请求体（<c>/images/generations</c>）。
    ///
    /// 刻意<b>不</b>发送 <c>response_format</c>：各家兼容网关对它的支持不一致，
    /// 省略时服务端默认返回 <c>url</c>，而本框架本来就要把图片下载到本地存储，
    /// 所以省略它只会多一次 GET，却换来最大的兼容性（也顺带兼容 b64_json 返回）。
    /// </summary>
    public static string OpenAiImages(
        ImageDeliveryRequest request,
        string? size = null,
        bool includeDiffusionParameters = true,
        bool includeOptionalParameters = true)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = string.IsNullOrWhiteSpace(request.ModelKey) ? "image-1" : request.ModelKey,
            ["prompt"] = request.Prompt,
            ["n"] = 1,
            ["size"] = size ?? request.OpenAiSize,
        };

        // steps / cfg_scale 是 Stable Diffusion 系的参数，通义万相这类模型不认，
        // 部分网关遇到未知字段会直接报 InvalidParameter，因此按协议决定是否携带。
        if (includeDiffusionParameters)
        {
            payload["steps"] = request.Steps;
            payload["cfg_scale"] = request.CfgScale;
        }

        if (includeOptionalParameters)
        {
            if (!string.IsNullOrWhiteSpace(request.NegativePrompt))
                payload["negative_prompt"] = request.NegativePrompt;

            if (request.EffectiveSeed is { } seed)
                payload["seed"] = seed;
        }

        return JsonSerializer.Serialize(payload, ModelDeliveryJson.Options);
    }

    /// <summary>
    /// 极简请求体：只保留各网关都一定认识的 <c>model / prompt / n / size</c>。
    /// 用于首个请求被网关以「未知参数」拒绝（400 InvalidParameter）时的兜底重试——
    /// 异构网关（尤其云端模型，如百炼的 negative_prompt 仅部分模型支持）对这种差异很敏感。
    /// </summary>
    public static string OpenAiImagesMinimal(ImageDeliveryRequest request, string? size = null)
        => OpenAiImages(request, size, includeDiffusionParameters: false, includeOptionalParameters: false);

    /// <summary>
    /// DashScope 原生<b>同步</b>接口（<c>multimodal-generation/generation</c>，qwen-image 系列）的请求体：
    /// <code>
    /// {
    ///   "model": "qwen-image-3.0-pro",
    ///   "input": { "messages": [ { "role": "user", "content": [ { "text": "..." } ] } ] },
    ///   "parameters": { "size": "1024*1024", "n": 1 }
    /// }
    /// </code>
    /// 图片在响应体的 <c>output.choices[].message.content[].image</c>。
    /// 尺寸同样使用星号写法（DashScope 协议统一规则）。
    /// </summary>
    public static string DashScopeSync(ImageDeliveryRequest request)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["size"] = request.DashScopeSize,
            ["n"] = 1,
        };

        // negative_prompt 仅 qwen-image-3.0 系列支持，被拒时框架会自动降级成极简体重试
        if (!string.IsNullOrWhiteSpace(request.NegativePrompt))
            parameters["negative_prompt"] = request.NegativePrompt;

        if (request.EffectiveSeed is { } seed)
            parameters["seed"] = seed;

        return JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                ["model"] = request.ModelKey ?? string.Empty,
                ["input"] = BuildMultimodalInput(request),
                ["parameters"] = parameters,
            },
            ModelDeliveryJson.Options);
    }

    /// <summary>
    /// 原生同步接口的极简体：<c>parameters</c> 整体是可选的，
    /// 丢掉它即可绕开"某个可选字段不被支持"导致的 400。
    /// </summary>
    public static string DashScopeSyncMinimal(ImageDeliveryRequest request)
        => JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                ["model"] = request.ModelKey ?? string.Empty,
                ["input"] = BuildMultimodalInput(request),
            },
            ModelDeliveryJson.Options);

    /// <summary>
    /// 多模态接口的 input 结构。只支持单轮对话，因此 messages 有且只有一个 user 元素。
    ///
    /// 文生图时 content 只有一段 text；图生图时在 text 之后追加若干段 image，
    /// 这是百炼多模态图像编辑（qwen-image-edit / qwen-image）约定的输入顺序
    /// ——先说要改什么，再给底图。
    /// </summary>
    private static Dictionary<string, object?> BuildMultimodalInput(ImageDeliveryRequest request)
    {
        var content = new List<object> { new Dictionary<string, object?> { ["text"] = request.Prompt } };

        foreach (var reference in request.References.Take(ImageDeliveryRequest.MaxReferenceImages))
            content.Add(new Dictionary<string, object?> { ["image"] = reference.DataUri });

        return new Dictionary<string, object?>
        {
            ["messages"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    ["content"] = content,
                },
            },
        };
    }

    /// <summary>
    /// OpenAI 系「图生图」的 multipart 表单（对应 <c>/images/edits</c>）。
    ///
    /// 该接口只接受 multipart/form-data，图片必须以文件字段（<c>image</c>）提交，
    /// 因此不能复用 JSON 请求体。多张参考图时重复同一个字段名，
    /// 主流兼容网关都能识别为图片数组。
    ///
    /// <paramref name="minimal" /> 为真时只保留 model / prompt / n / size / image，
    /// 用于首个请求被网关以「未知字段」拒绝后的降级重试。
    /// </summary>
    public static MultipartFormDataContent OpenAiEditsForm(ImageDeliveryRequest request, bool minimal = false)
    {
        var form = new MultipartFormDataContent();

        form.Add(new StringContent(string.IsNullOrWhiteSpace(request.ModelKey) ? "image-1" : request.ModelKey), "model");
        form.Add(new StringContent(request.Prompt), "prompt");
        form.Add(new StringContent("1"), "n");
        form.Add(new StringContent(request.OpenAiSize), "size");

        foreach (var reference in request.References.Take(ImageDeliveryRequest.MaxReferenceImages))
        {
            var image = new ByteArrayContent(reference.Bytes);
            image.Headers.ContentType = new MediaTypeHeaderValue(reference.ContentType);
            form.Add(image, "image", reference.FileName);
        }

        if (minimal)
            return form;

        form.Add(new StringContent(request.Steps.ToString(CultureInfo.InvariantCulture)), "steps");
        form.Add(new StringContent(request.CfgScale.ToString(CultureInfo.InvariantCulture)), "cfg_scale");

        if (!string.IsNullOrWhiteSpace(request.NegativePrompt))
            form.Add(new StringContent(request.NegativePrompt), "negative_prompt");

        if (request.EffectiveSeed is { } seed)
            form.Add(new StringContent(seed.ToString(CultureInfo.InvariantCulture)), "seed");

        return form;
    }

    /// <summary>
    /// DashScope 原生<b>异步</b>接口的请求体：
    /// <c>{"model": "...", "input": {"prompt": "..."}, "parameters": {"size": "1024*1024", "n": 1}}</c>
    /// </summary>
    public static string DashScopeAsync(ImageDeliveryRequest request)
    {
        var input = new Dictionary<string, object?>
        {
            ["prompt"] = request.Prompt,
        };

        if (!string.IsNullOrWhiteSpace(request.NegativePrompt))
            input["negative_prompt"] = request.NegativePrompt;

        var parameters = new Dictionary<string, object?>
        {
            ["size"] = request.DashScopeSize,
            ["n"] = 1,
        };

        if (request.EffectiveSeed is { } seed)
            parameters["seed"] = seed;

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.ModelKey ?? string.Empty,
            ["input"] = input,
            ["parameters"] = parameters,
        };

        return JsonSerializer.Serialize(payload, ModelDeliveryJson.Options);
    }

    /// <summary>
    /// 百炼「图片翻译」接口（<c>image2image/image-synthesis</c>，qwen-mt-image 系列）的请求体：
    /// <code>
    /// {
    ///   "model": "qwen-mt-image-2.0",
    ///   "input": {
    ///     "image_url": "https://.../source.png",
    ///     "source_lang": "auto",
    ///     "target_lang": "en"
    ///   }
    /// }
    /// </code>
    ///
    /// 与其他入口的三点关键差异（每一条都会直接决定 400 还是成功）：
    /// <list type="number">
    ///   <item><b>没有 prompt 字段</b>。这个接口翻译的是「图里的文字」，
    ///         不是「按描述画一张图」，因此 <see cref="ImageDeliveryRequest.Prompt" />
    ///         不能作为绘图提示词发出去；</item>
    ///   <item>图片走 <c>input.image_url</c>（<b>公网可访问地址</b>），不是 base64 data URI；
    ///         本站 <c>/uploads</c> 下的文件公网访问不到，必须换成本站对外可访问的地址；</li>
    ///   <item>没有 <c>size</c> / <c>n</c> / <c>seed</c> 等参数，输出尺寸与原图一致。</item>
    /// </list>
    ///
    /// 语种从提示词里解析（形如"翻译成英文""中译日"）；
    /// 解析不出来时源语言用 <c>auto</c> 自动检测，目标语言默认英文。
    /// </summary>
    public static string DashScopeImageTranslate(ImageDeliveryRequest request)
    {
        var input = new Dictionary<string, object?>
        {
            // 翻译模型只认公网 URL。这里把本地相对地址原样送出，
            // 由上层在发请求前校验是否为公网地址（IsPubliclyAccessibleReference）。
            ["image_url"] = request.References.FirstOrDefault()?.Url ?? string.Empty,
            ["source_lang"] = ResolveSourceLang(request.Prompt),
            ["target_lang"] = ResolveTargetLang(request.Prompt),
        };

        return JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                ["model"] = request.ModelKey ?? string.Empty,
                ["input"] = input,
            },
            ModelDeliveryJson.Options);
    }

    /// <summary>
    /// 从提示词里识别源语言。<c>auto</c> 表示由模型自动检测，
    /// 这是默认值——大多数翻译请求只说了目标语言，没说源语言。
    /// </summary>
    private static string ResolveSourceLang(string? prompt)
        => prompt is null ? "auto" : FindLanguage(prompt, beforeMarker: true) ?? "auto";

    /// <summary>
    /// 从提示词里识别目标语言。识别不到时默认英文（百炼示例里最常见的目标语言）。
    /// </summary>
    private static string ResolveTargetLang(string? prompt)
        => prompt is null ? "en" : FindLanguage(prompt, beforeMarker: false) ?? "en";

    /// <summary>
    /// 「把 A 翻译成 B」里的分界词。以它的位置区分谁是源语言、谁是目标语言。
    ///
    /// 不做自然语言理解，只按"分界词前后各找一个语种关键词"来判断——
    /// 对翻译指令这种高度模板化的说法已经足够，且绝不会把
    /// "翻译成英文"里的"英文"误判成源语言。
    /// </summary>
    private static readonly string[] TranslateMarkers =
    [
        "翻译成", "翻译为", "译成", "译为", "转成", "转为",
        "翻译", "into", "to ",
    ];

    /// <summary>语种关键词 → ISO 语言码。只收全称，不收 "en"/"zh" 这类两字母码</summary>
    private static readonly (string Code, string[] Words)[] LanguageWords =
    [
        ("zh", ["中文", "汉语", "简体", "繁体", "chinese"]),
        ("en", ["英文", "英语", "english"]),
        ("ja", ["日文", "日语", "japanese"]),
        ("ko", ["韩文", "韩语", "韩文", "korean"]),
        ("fr", ["法文", "法语", "french"]),
        ("de", ["德文", "德语", "german"]),
        ("es", ["西班牙文", "西班牙语", "西语", "spanish"]),
        ("ru", ["俄文", "俄语", "russian"]),
        ("it", ["意大利文", "意大利语", "意语", "italian"]),
        ("pt", ["葡萄牙文", "葡萄牙语", "葡语", "portuguese"]),
        ("th", ["泰文", "泰语", "thai"]),
        ("vi", ["越南文", "越南语", "vietnamese"]),
        ("ar", ["阿拉伯文", "阿拉伯语", "arabic"]),
    ];

    /// <summary>
    /// 在提示词的指定半区里找语种关键词。
    /// <paramref name="beforeMarker" /> 为真时只搜分界词之前（源语言），否则只搜之后（目标语言）。
    /// </summary>
    private static string? FindLanguage(string prompt, bool beforeMarker)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return null;

        // 找不到分界词时，把整句当作同一半区处理
        var split = IndexOfTranslateMarker(prompt);
        var region = split < 0
            ? prompt
            : (beforeMarker ? prompt[..split] : prompt[(split + TranslateMarkerLength(prompt, split))..]);

        return LanguageWords
            .Select(pair => (pair.Code, Index: IndexOfAnyWord(region, pair.Words)))
            .Where(found => found.Index >= 0)
            .OrderByDescending(found => found.Index)
            .Select(found => found.Code)
            .FirstOrDefault();
    }

    /// <summary>找第一个出现的分界词位置；都没出现返回 -1</summary>
    private static int IndexOfTranslateMarker(string prompt)
        => TranslateMarkers
            .Select(marker => prompt.IndexOf(marker, StringComparison.OrdinalIgnoreCase))
            .Where(index => index >= 0)
            .DefaultIfEmpty(-1)
            .Min();

    /// <summary>取当前位置命中的分界词长度（多词匹配时取最长的那个）</summary>
    private static int TranslateMarkerLength(string prompt, int index)
    {
        var match = TranslateMarkers
            .Where(marker => index + marker.Length <= prompt.Length
                             && prompt.AsSpan(index, marker.Length).Equals(marker, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(marker => marker.Length)
            .FirstOrDefault();

        return match?.Length ?? 0;
    }

    /// <summary>找任一关键词首次出现的位置；都没出现返回 -1</summary>
    private static int IndexOfAnyWord(string text, string[] words)
        => words
            .Select(word => text.IndexOf(word, StringComparison.OrdinalIgnoreCase))
            .Where(index => index >= 0)
            .DefaultIfEmpty(-1)
            .Min();
}
