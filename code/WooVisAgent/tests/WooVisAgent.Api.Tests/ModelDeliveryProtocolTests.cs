using WooVisAgent.SharedLib.Delivery;
using WooVisAgent.SharedLib.Domain;
using Xunit;

namespace WooVisAgent.Api.Tests;

/// <summary>
/// 模型交付协议解析的单测：覆盖各类供应商 / endpoint 形态，
/// 确认解析出的请求地址、请求体结构、异步轮询地址、以及兜底方案链条都正确。
/// </summary>
public class ModelDeliveryProtocolTests
{
    private readonly ModelDeliveryProtocolResolver _resolver = ModelDeliveryProtocols.CreateDefault();

    private static ModelConfig Model(string provider, string? endpoint, string? modelKey = null)
        => new()
        {
            Name = "测试模型",
            Provider = provider,
            Category = ModelCategory.Image,
            Endpoint = endpoint,
            ModelKey = modelKey,
        };

    private static ImageDeliveryRequest Request(string? modelKey = "wanx2.1-t2i-turbo")
        => new(
            ModelKey: modelKey,
            Prompt: "一只坐在窗台的橘猫",
            NegativePrompt: "模糊",
            Width: 1024,
            Height: 1024,
            Steps: 30,
            CfgScale: 7,
            Seed: 42,
            Index: 0);

    /// <summary>取某个模型在首选方案之后声明的全部兜底方案地址</summary>
    private List<string> FallbackUrls(ModelConfig model)
    {
        var resolution = _resolver.Resolve(model);
        Assert.NotNull(resolution);
        return [.. resolution!.Protocol.GetFallbackProtocols(model).Select(p => p.ResolveRequestUrl(model)!)];
    }

    [Theory]
    // —— OpenAI 官方 ——
    [InlineData("OpenAI", "https://api.openai.com", null, "https://api.openai.com/v1/images/generations")]
    [InlineData("OpenAI", "https://api.openai.com/v1", null, "https://api.openai.com/v1/images/generations")]
    [InlineData("OpenAI", "https://api.openai.com/v1/images/generations", null, "https://api.openai.com/v1/images/generations")]
    // —— 其他 OpenAI 兼容网关（带版本路径的 base）——
    [InlineData("SiliconFlow", "https://api.siliconflow.cn/v1", null, "https://api.siliconflow.cn/v1/images/generations")]
    [InlineData("Custom", "https://ark.cn-beijing.volces.com/api/v3", null, "https://ark.cn-beijing.volces.com/api/v3/images/generations")]
    [InlineData("Custom", "https://my-gw.example.com:8080", null, "https://my-gw.example.com:8080/v1/images/generations")]
    // —— 自建网关：用户填了完整生成路径，必须原样使用 ——
    [InlineData("Custom", "https://my-gw.example.com/api/generate-image", null, "https://my-gw.example.com/api/generate-image")]
    [InlineData("Custom", "https://my-gw.example.com/v2/text2image", null, "https://my-gw.example.com/v2/text2image")]
    public void Resolve_ShouldBuildExpectedUrl(string provider, string? endpoint, string? modelKey, string expected)
    {
        var url = _resolver.ResolveRequestUrl(Model(provider, endpoint, modelKey), out _);
        Assert.Equal(expected, url);
    }

    [Fact]
    public void Resolve_OpenAiAzure_ShouldUseDeploymentsPath()
    {
        var url = _resolver.ResolveRequestUrl(
            Model("OpenAI", "https://myres.openai.azure.com", "gpt-image-1"),
            out var protocol);

        Assert.Equal("azure-openai", protocol);
        Assert.Contains("/openai/deployments/gpt-image-1/images/generations", url);
        Assert.Contains("api-version=", url);
    }

    [Fact]
    public void Resolve_EmptyEndpoint_ShouldReturnNull()
    {
        var url = _resolver.ResolveRequestUrl(Model("Custom", ""), out var protocol);
        Assert.Null(url);
        Assert.Null(protocol);
    }

    // ───────────────────────── 百炼：本次 404 故障的修复点 ─────────────────────────

    /// <summary>
    /// 本次线上故障的原始现场：地址是 <c>/compatible-mode/v1</c>，
    /// 旧实现直接补 <c>/images/generations</c> 后必然 404。
    /// 现在它只是"候选方案之一"，失败后框架会自动落到百炼原生接口。
    /// </summary>
    [Theory]
    [InlineData("https://dashscope.aliyuncs.com/compatible-mode/v1")]
    [InlineData("https://dashscope.aliyuncs.com/compatible-mode/v1/")]
    [InlineData("https://ws1234.cn-beijing.maas.aliyuncs.com/compatible-mode/v1")]
    public void Resolve_AliyunCompatibleMode_ShouldPreferCompatibleEntry(string endpoint)
    {
        var url = _resolver.ResolveRequestUrl(Model("通义万相", endpoint), out var protocol);

        Assert.Equal("aliyun-dashscope-compatible", protocol);
        Assert.EndsWith("/compatible-mode/v1/images/generations", url);
        // 关键回归点：不能再出现 /compatible-mode/v1/services/aigc/... 这种必然 404 的组合
        Assert.DoesNotContain("/services/aigc/", url);
    }

    /// <summary>
    /// 兼容入口若不可用，必须能自动落到百炼原生接口上（qwen-image 走同步原生）。
    ///
    /// 首选方案已是原生同步，所以兜底链里应当<b>不含</b>原生同步自身，只剩异步入口。
    /// </summary>
    [Fact]
    public void Fallbacks_AliyunCompatibleMode_QwenModel_ShouldIncludeNativeAsyncOnly()
    {
        var fallbacks = FallbackUrls(Model("通义万相", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-image-3.0-pro"));

        Assert.Equal("https://dashscope.aliyuncs.com/api/v1/services/aigc/text2image/image-synthesis", fallbacks[0]);
        // 首选已是原生同步，不该把它重复列进兜底链
        Assert.DoesNotContain(
            "https://dashscope.aliyuncs.com/api/v1/services/aigc/multimodal-generation/generation",
            fallbacks);
    }

    /// <summary>wan / wanx 系列则优先原生异步（提交 + 轮询）</summary>
    [Fact]
    public void Fallbacks_AliyunCompatibleMode_WanModel_ShouldPreferNativeAsync()
    {
        var fallbacks = FallbackUrls(Model("通义万相", "https://dashscope.aliyuncs.com/compatible-mode/v1", "wan2.5-t2i-preview"));

        Assert.Equal("https://dashscope.aliyuncs.com/api/v1/services/aigc/text2image/image-synthesis", fallbacks[0]);
        Assert.Contains("https://dashscope.aliyuncs.com/api/v1/services/aigc/multimodal-generation/generation", fallbacks);
    }

    /// <summary>原生 /api/v1 入口 + qwen-image → 同步多模态接口，且不轮询</summary>
    [Fact]
    public void Resolve_AliyunNative_QwenImage_ShouldUseSyncMultimodal()
    {
        var model = Model("AliyunBailian", "https://dashscope.aliyuncs.com/api/v1", "qwen-image-3.0-pro");
        var resolution = _resolver.Resolve(model);

        Assert.NotNull(resolution);
        Assert.Equal("aliyun-dashscope-sync", resolution!.Protocol.Name);
        Assert.Equal(
            "https://dashscope.aliyuncs.com/api/v1/services/aigc/multimodal-generation/generation",
            resolution.Url);
        Assert.False(resolution.Protocol.IsAsynchronous);
        Assert.Null(resolution.Protocol.ResolveTaskStatusUrl(model, "whatever"));
    }

    /// <summary>原生 /api/v1 入口 + wan → 异步文生图接口，带 X-DashScope-Async 并轮询</summary>
    [Fact]
    public void Resolve_AliyunNative_WanModel_ShouldUseAsyncSynthesis()
    {
        var model = Model("AliyunBailian", "https://dashscope.aliyuncs.com/api/v1", "wan2.5-t2i-preview");
        var resolution = _resolver.Resolve(model);

        Assert.NotNull(resolution);
        var protocol = resolution!.Protocol;

        Assert.Equal("aliyun-dashscope-async", protocol.Name);
        Assert.Equal(
            "https://dashscope.aliyuncs.com/api/v1/services/aigc/text2image/image-synthesis",
            resolution.Url);
        Assert.True(protocol.IsAsynchronous);
        Assert.Equal("enable", protocol.BuildExtraHeaders(Request())["X-DashScope-Async"]);
        Assert.Equal(
            "https://dashscope.aliyuncs.com/api/v1/tasks/abc-123",
            protocol.ResolveTaskStatusUrl(model, "abc-123"));
    }

    /// <summary>
    /// 用户直接填了完整原生路径：归一化后必须与"只填到 /api/v1"得到同一个请求地址，
    /// 即 <c>{根地址}/api/v1/services/aigc/text2image/image-synthesis</c>。
    /// </summary>
    [Theory]
    [InlineData("https://dashscope.aliyuncs.com/api/v1/services/aigc/text2image/image-synthesis")]
    [InlineData("https://dashscope.aliyuncs.com/api/v1/services/aigc/text2image")]
    [InlineData("https://dashscope.aliyuncs.com/api/v1")]
    [InlineData("https://dashscope.aliyuncs.com")]
    public void Resolve_AliyunFullNativePath_ShouldNormalizeToSameBase(string endpoint)
    {
        var model = Model("Custom", endpoint, "wan2.5-t2i-preview");
        var resolution = _resolver.Resolve(model);

        Assert.NotNull(resolution);
        Assert.Equal(
            "https://dashscope.aliyuncs.com/api/v1/services/aigc/text2image/image-synthesis",
            resolution!.Url);
    }

    /// <summary>
    /// 兼容入口填到完整路径时，<b>模型名优先于地址形态</b>。
    ///
    /// 踩过的坑：qwen-image 系列并没有 compatible-mode 的图生图入口，
    /// 一旦按地址形态优先派到 /images/edits，公共域名会在收到 multipart 请求体之前
    /// 直接断开连接，客户端只看到一句无从下手的 "Error while copying content to a stream."。
    /// 因此模型名能判定时一律以模型名为准（原生同步 multimodal 入口）。
    /// </summary>
    [Fact]
    public void Resolve_AliyunCompatibleFullPath_QwenModel_ShouldPreferNativeSync()
    {
        var url = _resolver.ResolveRequestUrl(
            Model("通义万相", "https://dashscope.aliyuncs.com/compatible-mode/v1/images/generations", "qwen-image-3.0-pro"),
            out var protocol);

        Assert.Equal("aliyun-dashscope-sync", protocol);
        Assert.Equal("https://dashscope.aliyuncs.com/api/v1/services/aigc/multimodal-generation/generation", url);
    }

    /// <summary>模型名看不出系列（既非 qwen-image 也非 wan/wanx）时，才按地址形态走兼容入口</summary>
    [Fact]
    public void Resolve_AliyunCompatibleFullPath_UnknownModel_ShouldKeepCompatibleEntry()
    {
        var url = _resolver.ResolveRequestUrl(
            Model("通义万相", "https://dashscope.aliyuncs.com/compatible-mode/v1/images/generations", "some-image-model"),
            out var protocol);

        Assert.Equal("aliyun-dashscope-compatible", protocol);
        Assert.Equal("https://dashscope.aliyuncs.com/compatible-mode/v1/images/generations", url);
    }

    /// <summary>业务空间专属域名（新版推荐域名）同样能还原出正确的根地址</summary>
    [Fact]
    public void Resolve_AliyunWorkspaceDomain_ShouldResolve()
    {
        var model = Model("阿里云百炼", "https://ws1234.cn-beijing.maas.aliyuncs.com/api/v1", "wan2.5-t2i-preview");
        var resolution = _resolver.Resolve(model);

        Assert.NotNull(resolution);
        Assert.Equal(
            "https://ws1234.cn-beijing.maas.aliyuncs.com/api/v1/services/aigc/text2image/image-synthesis",
            resolution!.Url);
        Assert.Equal(
            "https://ws1234.cn-beijing.maas.aliyuncs.com/api/v1/tasks/t-1",
            resolution.Protocol.ResolveTaskStatusUrl(model, "t-1"));
    }

    // ───────────────────────── 请求体结构 ─────────────────────────

    /// <summary>原生同步（qwen-image）：input.messages 结构、尺寸用星号</summary>
    [Fact]
    public void BuildRequestBody_AliyunSync_ShouldUseMessagesAndStarSize()
    {
        var model = Model("AliyunBailian", "https://dashscope.aliyuncs.com/api/v1", "qwen-image-3.0-pro");
        var resolution = _resolver.Resolve(model);
        Assert.NotNull(resolution);

        var body = resolution!.Protocol.BuildRequestBody(Request("qwen-image-3.0-pro"));

        Assert.Contains("\"input\":{\"messages\":[{\"role\":\"user\",\"content\":[{\"text\":\"", body);
        Assert.Contains("\"size\":\"1024*1024\"", body);
        Assert.Contains("\"negative_prompt\":\"模糊\"", body);
        Assert.Contains("\"seed\":42", body);
        Assert.DoesNotContain("steps", body);
        Assert.DoesNotContain("cfg_scale", body);
    }

    /// <summary>原生同步的极简体：丢掉整个 parameters，用于绕开"可选字段不被支持"的 400</summary>
    [Fact]
    public void BuildMinimalRequestBody_AliyunSync_ShouldDropParameters()
    {
        var model = Model("AliyunBailian", "https://dashscope.aliyuncs.com/api/v1", "qwen-image-2.1-turbo");
        var resolution = _resolver.Resolve(model);
        Assert.NotNull(resolution);

        var minimal = resolution!.Protocol.BuildMinimalRequestBody(Request("qwen-image-2.1-turbo"));

        Assert.DoesNotContain("\"parameters\"", minimal);
        Assert.Contains("\"input\":{\"messages\":[", minimal);
        Assert.Contains("\"model\":\"qwen-image-2.1-turbo\"", minimal);
    }

    /// <summary>原生异步：input.prompt 结构 + 星号尺寸</summary>
    [Fact]
    public void BuildRequestBody_AliyunAsync_ShouldUsePromptAndStarSize()
    {
        var model = Model("AliyunBailian", "https://dashscope.aliyuncs.com/api/v1", "wan2.5-t2i-preview");
        var resolution = _resolver.Resolve(model);
        Assert.NotNull(resolution);

        var body = resolution!.Protocol.BuildRequestBody(Request("wan2.5-t2i-preview"));

        Assert.Contains("\"input\":{\"prompt\"", body);
        Assert.Contains("\"size\":\"1024*1024\"", body);
    }

    /// <summary>
    /// 兼容入口：参数平铺顶层、尺寸用字母 x、不含 steps / cfg_scale。
    ///
    /// 用<b>模型名看不出系列</b>的模型来验证——只有这种情况下才会按地址形态派到兼容入口。
    /// </summary>
    [Fact]
    public void BuildRequestBody_AliyunCompatible_ShouldUseTopLevelOpenAiShape()
    {
        var model = Model("通义万相", "https://dashscope.aliyuncs.com/compatible-mode/v1", "some-image-model");
        var resolution = _resolver.Resolve(model);
        Assert.NotNull(resolution);
        Assert.Equal("aliyun-dashscope-compatible", resolution!.Protocol.Name);

        var body = resolution.Protocol.BuildRequestBody(Request("some-image-model"));

        Assert.Contains("\"size\":\"1024x1024\"", body);
        Assert.DoesNotContain("*", body);
        Assert.DoesNotContain("steps", body);
        Assert.DoesNotContain("cfg_scale", body);
        Assert.DoesNotContain("\"input\"", body);
        Assert.Contains("\"negative_prompt\"", body);
        Assert.False(resolution.Protocol.IsAsynchronous);
    }

    // ───────────────────── 图片翻译模型（qwen-mt-image） ─────────────────────

    /// <summary>
    /// qwen-mt-image 是<b>图片翻译</b>模型，必须走 image2image/image-synthesis。
    ///
    /// 回归点：它的模型名同时含 "qwen" 和 "image"，若只按 "qwen" 分派会被送到
    /// multimodal-generation/generation，而该模型的请求体里根本没有 prompt 字段，
    /// 百炼只会回一句 InvalidParameter: url error，真实原因完全被掩盖。
    /// </summary>
    [Fact]
    public void Resolve_QwenMtImage_ShouldUseImage2ImageSynthesis()
    {
        var resolution = _resolver.Resolve(Model("AliyunBailian", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-mt-image-2.0"));

        Assert.NotNull(resolution);
        Assert.Equal("aliyun-dashscope-image2image", resolution!.Protocol.Name);
        Assert.Equal(
            "https://dashscope.aliyuncs.com/api/v1/services/aigc/image2image/image-synthesis",
            resolution.Url);
    }

    /// <summary>图片翻译模型不支持纯文生图：声明后框架才能在发请求前拦下来</summary>
    [Fact]
    public void Resolve_QwenMtImage_ShouldNotSupportTextToImage()
    {
        var resolution = _resolver.Resolve(Model("AliyunBailian", "https://dashscope.aliyuncs.com/api/v1", "qwen-mt-image-2.0"));

        Assert.NotNull(resolution);
        Assert.False(resolution!.Protocol.SupportsTextToImage);
        // 但图生图（即"上传图片翻译"）是支持的
        Assert.True(resolution.Protocol.SupportsImageToImage);
    }

    /// <summary>翻译模型只有唯一一条路，不应挂兜底方案（挂了在图生图时反而会误导排查方向）</summary>
    [Fact]
    public void Fallbacks_QwenMtImage_ShouldBeEmpty()
    {
        var model = Model("AliyunBailian", "https://dashscope.aliyuncs.com/api/v1", "qwen-mt-image-2.0");
        var resolution = _resolver.Resolve(model);

        Assert.NotNull(resolution);
        Assert.Empty(resolution!.Protocol.GetFallbackProtocols(model));
    }

    /// <summary>翻译接口请求体：input.image_url + 两种语言，没有 prompt / size</summary>
    [Fact]
    public void BuildRequestBody_QwenMtImage_ShouldUseImageUrlAndLanguages()
    {
        var model = Model("AliyunBailian", "https://dashscope.aliyuncs.com/api/v1", "qwen-mt-image-2.0");
        var resolution = _resolver.Resolve(model);
        Assert.NotNull(resolution);

        var request = new ImageDeliveryRequest(
            ModelKey: "qwen-mt-image-2.0",
            Prompt: "把这张图翻译成英文",
            NegativePrompt: null,
            Width: 1024,
            Height: 1024,
            Steps: 30,
            CfgScale: 7,
            Seed: 42,
            Index: 0,
            ReferenceImages:
            [
                new ImageDeliveryReference(
                    "https://example.com/source.png",
                    [1, 2, 3],
                    "image/png",
                    "source.png"),
            ]);

        var body = resolution!.Protocol.BuildRequestBody(request);

        Assert.Contains("\"model\":\"qwen-mt-image-2.0\"", body);
        Assert.Contains("\"image_url\":\"https://example.com/source.png\"", body);
        Assert.Contains("\"target_lang\":\"en\"", body);
        // 源语言没在提示词里说明 → auto 自动检测
        Assert.Contains("\"source_lang\":\"auto\"", body);
        // 关键：翻译接口不吃绘图提示词
        Assert.DoesNotContain("翻译成英文", body);
        Assert.DoesNotContain("\"size\"", body);
    }

    /// <summary>提示词里说明了源语言时，应正确识别出来</summary>
    [Theory]
    [InlineData("中文翻译成日文", "zh", "ja")]
    [InlineData("英文翻译成中文", "en", "zh")]
    public void BuildRequestBody_QwenMtImage_ShouldResolveLanguagesFromPrompt(string prompt, string source, string target)
    {
        var model = Model("AliyunBailian", "https://dashscope.aliyuncs.com/api/v1", "qwen-mt-image-2.0");
        var resolution = _resolver.Resolve(model);

        var request = new ImageDeliveryRequest(
            ModelKey: "qwen-mt-image-2.0",
            Prompt: prompt,
            NegativePrompt: null,
            Width: 1024,
            Height: 1024,
            Steps: 30,
            CfgScale: 7,
            Seed: 42,
            Index: 0,
            ReferenceImages:
            [
                new ImageDeliveryReference("https://example.com/a.png", [1], "image/png", "a.png"),
            ]);

        var body = resolution!.Protocol.BuildRequestBody(request);

        Assert.Contains($"\"source_lang\":\"{source}\"", body);
        Assert.Contains($"\"target_lang\":\"{target}\"", body);
    }

    /// <summary>同步协议不应提供任务轮询地址</summary>
    [Fact]
    public void SyncProtocol_ShouldNotExposeTaskStatusUrl()
    {
        var model = Model("OpenAI", "https://api.openai.com/v1");
        var resolution = _resolver.Resolve(model);

        Assert.NotNull(resolution);
        Assert.False(resolution!.Protocol.IsAsynchronous);
        Assert.Null(resolution.Protocol.ResolveTaskStatusUrl(model, "whatever"));
        Assert.Empty(resolution.Protocol.GetFallbackProtocols(model));
    }
}
