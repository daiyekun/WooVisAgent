using WooVisAgent.SharedLib.Domain;

namespace WooVisAgent.SharedLib.Delivery;

/// <summary>
/// 百炼（DashScope / Model Studio）地址的识别与归一化。
///
/// 用户可能把地址填成很多种形态，都必须能还原出同一个「服务根地址」，再由方案协议拼各自的原生路径：
/// <list type="bullet">
///   <item><c>https://dashscope.aliyuncs.com</c>（纯域名）</item>
///   <item><c>https://dashscope.aliyuncs.com/api/v1</c>（原生版本前缀）</item>
///   <item><c>https://dashscope.aliyuncs.com/compatible-mode/v1</c>（OpenAI 兼容入口）</item>
///   <item><c>https://{WorkspaceId}.cn-beijing.maas.aliyuncs.com/compatible-mode/v1</c>（业务空间专属域名）</item>
///   <item><c>.../api/v1/services/aigc/text2image/image-synthesis</c>（已经填到完整接口路径）</item>
/// </list>
/// </summary>
public static class AliyunDashScopeEndpoints
{
    /// <summary>
    /// 已知的「版本前缀 / 服务路径」结尾。归一化时从长到短反复剥离，
    /// 直到路径不再变化——这样"填到完整接口路径"和"只填到 /v1"能收敛到同一个根地址。
    /// 顺序无关紧要，使用前会按长度降序排序。
    /// </summary>
    private static readonly string[] KnownSuffixes =
    [
        "/api/v1/services/aigc/multimodal-generation/generation",
        "/api/v1/services/aigc/image-generation/generation",
        "/api/v1/services/aigc/image2image/image-synthesis",
        "/api/v1/services/aigc/text2image/image-synthesis",
        "/compatible-mode/v1/images/generations",
        "/compatible-mode/images/generations",
        "/api/v1/services/aigc/text2image",
        "/api/v1/services/aigc/image2image",
        "/api/v1/services/aigc",
        "/v1/images/generations",
        "/images/generations",
        "/compatible-mode/v1",
        "/compatible-mode",
        "/api/v1",
        "/v1",
    ];

    private static readonly string[] SuffixesByLengthDesc =
        [.. KnownSuffixes.OrderByDescending(x => x.Length)];

    /// <summary>是否应该由百炼协议族来投递这个模型</summary>
    public static bool IsDashScopeEndpoint(ModelConfig model)
    {
        var endpoint = model.Endpoint?.Trim();

        if (!string.IsNullOrEmpty(endpoint))
        {
            if (endpoint.Contains("/compatible-mode", StringComparison.OrdinalIgnoreCase))
                return true;

            if (endpoint.Contains("/services/aigc/", StringComparison.OrdinalIgnoreCase))
                return true;

            if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && IsDashScopeHost(uri.Host))
                return true;
        }

        return IsAliyunProvider(model.Provider);
    }

    /// <summary>
    /// 还原服务根地址（scheme + authority + 自定义前缀）。
    /// 地址不是合法的 http(s) 绝对地址时返回 <c>false</c>。
    /// </summary>
    public static bool TryResolveBase(string? endpoint, out string baseUrl)
    {
        baseUrl = string.Empty;

        if (string.IsNullOrWhiteSpace(endpoint))
            return false;

        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        var path = uri.AbsolutePath.TrimEnd('/');

        // 反复剥离已知结尾（自建网关的自定义前缀会被保留下来）
        var stripping = true;
        while (stripping && path.Length > 0)
        {
            stripping = false;

            foreach (var suffix in SuffixesByLengthDesc)
            {
                if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    path = path[..^suffix.Length].TrimEnd('/');
                    stripping = true;
                    break;
                }
            }
        }

        baseUrl = $"{uri.Scheme}://{uri.Authority}{path}";
        return true;
    }

    /// <summary>
    /// 按模型名判断优先走哪套原生入口。
    ///
    /// qwen-image 系列（多模态同步）与 wan / wanx 系列（文生图异步）的接口完全不同，
    /// 猜错的代价是一次 400，框架会据此自动切到另一套方案，所以这里取"更可能"的一个即可。
    ///
    /// <para>
    /// <b>qwen-mt-image（图片翻译）必须最先判定</b>：它的模型名同时含 "qwen" 和 "image"，
    /// 若只按 "qwen" 分派会被送到多模态生成入口，而翻译模型的请求体里根本没有 prompt，
    /// 百炼只会回一句 <c>url error, please check url</c>，真实原因完全被掩盖。
    /// </para>
    /// </summary>
    public static DashScopeImageMode PreferMode(string? modelKey)
    {
        var key = modelKey?.Trim().ToLowerInvariant() ?? string.Empty;

        // 图片翻译：输入模态只有图片，没有纯文生图入口
        if (IsImageTranslateModel(key))
            return DashScopeImageMode.Image2ImageTranslate;

        if (key.Contains("qwen", StringComparison.Ordinal))
            return DashScopeImageMode.SyncMultimodal;

        return DashScopeImageMode.AsyncText2Image;
    }

    /// <summary>
    /// 是否是「图片翻译」模型（qwen-mt-image / qwen-mt-image-2.0）。
    ///
    /// 模型名里 <c>mt</c> = machine translation，这是百炼自己的命名习惯
    /// （与文本翻译模型 qwen-mt 同源）。除此之外，名称里出现
    /// <c>image-translate</c> / <c>image_translate</c> 也一并视为翻译模型。
    /// </summary>
    public static bool IsImageTranslateModel(string? modelKey)
    {
        var key = modelKey?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(key))
            return false;

        if (!key.Contains("image", StringComparison.Ordinal))
            return false;

        return key.Contains("qwen-mt", StringComparison.Ordinal)
               || key.Contains("image-translate", StringComparison.Ordinal)
               || key.Contains("image_translate", StringComparison.Ordinal);
    }

    /// <summary>公共域名、国际站域名，以及业务空间专属域名 *.maas.aliyuncs.com</summary>
    public static bool IsDashScopeHost(string host)
        => host.Contains("dashscope", StringComparison.OrdinalIgnoreCase)
           || host.Contains("maas.aliyuncs.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>供应方字段是否指向阿里云百炼（用户可能填中文名）</summary>
    public static bool IsAliyunProvider(string? provider)
    {
        var value = provider?.Trim();
        if (string.IsNullOrEmpty(value))
            return false;

        return value.Equals("AliyunBailian", StringComparison.OrdinalIgnoreCase)
               || value.Equals("Aliyun", StringComparison.OrdinalIgnoreCase)
               || value.Equals("DashScope", StringComparison.OrdinalIgnoreCase)
               || value.Equals("ModelStudio", StringComparison.OrdinalIgnoreCase)
               || value.Equals("通义万相", StringComparison.OrdinalIgnoreCase)
               || value.Equals("通义", StringComparison.OrdinalIgnoreCase)
               || value.Equals("阿里云百炼", StringComparison.OrdinalIgnoreCase)
               || value.Equals("百炼", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// 百炼图片生成的<b>二次分派器</b>：命中百炼地址后，按 模型名 + 地址形态 决定投递方案的优先级。
///
/// 这是本次 404 故障的修复落点。之前对 <c>/compatible-mode/v1</c> 补
/// <c>/images/generations</c> 是错的——该路径在公共域名上并不存在（必然 404）。
/// 现在改为：
/// <list type="number">
///   <item>填了 <c>/compatible-mode</c> → 先试兼容入口（业务空间专属域名上确实可用），
///         失败再自动落到原生接口；</item>
///   <item>其他形态 → 按模型名直接走原生同步（qwen-image）或原生异步（wan / wanx），
///         再把另一套原生入口作为兜底方案。</item>
/// </list>
/// </summary>
public sealed class AliyunDashScopeProtocol : IModelDeliveryProtocol
{
    public string Name => "aliyun-dashscope";

    public bool Matches(ModelConfig model) => AliyunDashScopeEndpoints.IsDashScopeEndpoint(model);

    /// <summary>二次分派：交给真正会使用的那个方案，保证请求体/异步语义/轮询地址都对上</summary>
    public IModelDeliveryProtocol SelectDeliveryProtocol(ModelConfig model) => BuildPlans(model)[0];

    public string? ResolveRequestUrl(ModelConfig model)
        => BuildPlans(model)[0].ResolveRequestUrl(model);

    /// <summary>
    /// 按优先级排出候选方案；第一项即首选。
    ///
    /// 由 <see cref="AliyunDashScopeImageProtocol"/> 反查自己的兄弟方案来实现兜底链条，
    /// 这样即便解析器已经把结果二次分派成了具体方案，仍然能拿到"其余入口"。
    /// </summary>
    internal static List<AliyunDashScopeImageProtocol> BuildPlans(ModelConfig model)
    {
        var preferred = AliyunDashScopeEndpoints.PreferMode(model.ModelKey);

        // 图片翻译模型只有唯一一条路：image2image/image-synthesis。
        // 给它挂兜底方案反而有害——翻译模型拿提示词去别的入口一定失败，
        // 会把「模型选错了」掩盖成「试了 3 套方案都失败」。
        if (preferred == DashScopeImageMode.Image2ImageTranslate)
            return [new AliyunDashScopeImageProtocol(DashScopeImageMode.Image2ImageTranslate)];

        var isNative = preferred == DashScopeImageMode.SyncMultimodal;

        var nativeOrder = isNative
            ? new[] { DashScopeImageMode.SyncMultimodal, DashScopeImageMode.AsyncText2Image }
            : new[] { DashScopeImageMode.AsyncText2Image, DashScopeImageMode.SyncMultimodal };

        // 兼容入口（/compatible-mode/v1/images/generations 与 /images/edits）只在
        //「模型名看不出是哪一类」时才作为首选。
        //
        // 原因（踩过的坑）：用户常把 endpoint 填成 /compatible-mode/v1，
        // 但 qwen-image 系列<b>并没有</b> compatible-mode 的图生图入口，
        // 一旦图生图被优先派到 /images/edits，公共域名会在收到 multipart 请求体
        // 之前就断开连接，客户端最终只看到
        // "Error while copying content to a stream."（真实原因是 Socket 10054），
        // 排查成本极高。而按百炼官方文档，qwen-image 的图生图必须走
        // 原生 multimodal 入口的 input.messages[].content 里的 base64 图片。
        //
        // 所以：模型名能判定时以模型名为准（原生入口优先），
        // 无法判定（既不是 qwen-image 也不是 wan/wanx）才把兼容入口排到前面。
        IEnumerable<DashScopeImageMode> modes = !isNative && IsCompatibleMode(model.Endpoint)
            ? new[] { DashScopeImageMode.CompatibleOpenAi }.Concat(nativeOrder)
            : nativeOrder;

        // 兼容入口仍然留在兜底链里：业务空间专属域名（*.maas.aliyuncs.com）上它确实可用。
        return [.. modes.Select(mode => new AliyunDashScopeImageProtocol(mode))];
    }

    private static bool IsCompatibleMode(string? endpoint)
        => endpoint?.Contains("/compatible-mode", StringComparison.OrdinalIgnoreCase) == true;
}
