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
public sealed record ImageDeliveryRequest(
    string? ModelKey,
    string Prompt,
    string? NegativePrompt,
    int Width,
    int Height,
    int Steps,
    double CfgScale,
    long? Seed,
    int Index)
{
    /// <summary>OpenAI 系的尺寸写法：<c>1024x1024</c></summary>
    public string OpenAiSize => $"{Width}x{Height}";

    /// <summary>DashScope（百炼）的尺寸写法：<c>1024*1024</c>（星号，不是字母 x）</summary>
    public string DashScopeSize => $"{Width}*{Height}";

    /// <summary>同批次内多张图按 seed 递推，避免一张重复生成多份</summary>
    public long? EffectiveSeed => Seed is { } seed ? seed + Index : null;
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
                ["input"] = BuildMultimodalInput(request.Prompt),
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
                ["input"] = BuildMultimodalInput(request.Prompt),
            },
            ModelDeliveryJson.Options);

    /// <summary>
    /// 多模态接口的 input 结构。当前仅支持单轮对话，
    /// 因此 messages 有且只有一个 user 元素，content 里只有一段 text（文生图）。
    /// </summary>
    private static Dictionary<string, object?> BuildMultimodalInput(string prompt)
        => new()
        {
            ["messages"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    ["content"] = new object[]
                    {
                        new Dictionary<string, object?> { ["text"] = prompt },
                    },
                },
            },
        };

    /// <summary>
    /// DashScope 原生异步接口的请求体：
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
}
