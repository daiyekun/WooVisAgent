using System.Text.Json;
using WooVisAgent.SharedLib.Domain;

namespace WooVisAgent.SharedLib.Delivery;

/// <summary>
/// 用户在模型配置里可写的「交付模板」。全部字段可选，
/// 只覆盖与预设不同的部分，其余继续沿用预设值。
///
/// 抽成独立的 DTO 而不是直接暴露 <see cref="VideoDeliverySpec" />，
/// 是为了让「配置什么都能填」和「内部怎么解析」两件事解耦：
/// 用户写错了字段名会在反序列化时被忽略并落一条警告，
/// 而不是让整个模型配置保存失败。
/// </summary>
public sealed class VideoDeliveryTemplate
{
    /// <summary>
    /// 预设名（openai-videos / dashscope-video / dashscope-legacy-video / siliconflow-video）。
    /// 留空时按 endpoint 与 modelKey 推断。
    /// </summary>
    public string? Preset { get; set; }

    /// <summary>覆盖提交路径</summary>
    public string? SubmitPath { get; set; }

    /// <summary>覆盖轮询路径模板</summary>
    public string? StatusPath { get; set; }

    /// <summary>覆盖轮询方法（GET / POST）</summary>
    public string? StatusMethod { get; set; }

    /// <summary>轮询用 POST 时，任务号要放进请求体的哪个字段</summary>
    public string? StatusIdField { get; set; }

    /// <summary>覆盖任务号字段路径</summary>
    public string? TaskIdPath { get; set; }

    /// <summary>覆盖状态字段路径</summary>
    public string? StatusValuePath { get; set; }

    /// <summary>覆盖「已完成」的状态值</summary>
    public string? SucceededStatus { get; set; }

    /// <summary>覆盖「失败」的状态值（逗号分隔）</summary>
    public string? FailedStatuses { get; set; }

    /// <summary>覆盖视频地址字段路径</summary>
    public string? VideoUrlPath { get; set; }

    /// <summary>覆盖错误信息字段路径</summary>
    public string? ErrorPath { get; set; }

    /// <summary>
    /// 覆盖产物取回方式：url / content / base64。
    /// <c>content</c> 表示响应体里没有 URL，需要另外调 ContentPath 下载二进制（OpenAI 的做法）。
    /// </summary>
    public string? OutputKind { get; set; }

    /// <summary>视频正文的下载路径模板（OutputKind=content 时必填）</summary>
    public string? ContentPath { get; set; }

    /// <summary>额外请求头（键值对），例如百炼的 <c>X-DashScope-Async: enable</c></summary>
    public Dictionary<string, string>? ExtraHeaders { get; set; }

    /// <summary>
    /// 覆盖请求体字段映射。
    /// 键是 <see cref="VideoRequestFieldMap" /> 的属性名（用 camelCase），
    /// 值是该字段在请求体里的点号路径。传 <c>null</c> / 空串表示「该预设不传这个字段」。
    /// </summary>
    public VideoRequestFieldTemplate? RequestFields { get; set; }

    /// <summary>覆盖分辨率表达形式：omit / pixel / tier / tierRatio / tierLower</summary>
    public string? ResolutionStyle { get; set; }

    /// <summary>
    /// 覆盖请求体形状：flat（扁平字段）/ contentArray（火山方舟那种数组）。
    /// 留空表示沿用预设。
    /// </summary>
    public string? BodyShape { get; set; }

    /// <summary>
    /// 覆盖能力声明：支持哪些模式、各类素材上限多少。
    ///
    /// Seedance 三个版本的能力差别很大（1.0 pro 只有首尾帧、2.0 参考图 9 张、2.5 可到 30 张），
    /// 预设只能给一个保守值，具体模型要放宽或收紧时改这里。
    /// </summary>
    public VideoCapabilitySpec? Capabilities { get; set; }
}

/// <summary>请求体字段映射的可配置形态</summary>
public sealed class VideoRequestFieldTemplate
{
    public string? Model { get; set; }
    public string? Prompt { get; set; }
    public string? NegativePrompt { get; set; }
    public string? FirstFrame { get; set; }
    public string? Duration { get; set; }
    public string? Resolution { get; set; }
    public string? Ratio { get; set; }
    public string? Fps { get; set; }
    public string? Seed { get; set; }
    public string? ExtraParamsContainer { get; set; }

    // ContentArray 形状专用
    public string? ContentArray { get; set; }
    public string? TextItemType { get; set; }
    public string? ImageItemType { get; set; }
    public string? VideoItemType { get; set; }
    public string? AudioItemType { get; set; }
    public string? UrlField { get; set; }
    public string? RoleField { get; set; }
    public string? RoleFirstFrame { get; set; }
    public string? RoleLastFrame { get; set; }
    public string? RoleReferenceImage { get; set; }
    public string? RoleReferenceVideo { get; set; }
    public string? RoleReferenceAudio { get; set; }

    /// <summary>
    /// 素材地址是否与 <c>type</c> 同级。
    /// 百炼 2.7 的 media 数组是 <c>{"type":"first_frame","url":"..."}</c>（平铺），
    /// 火山方舟的 content 是 <c>{"type":"image_url","image_url":{"url":"..."}}</c>（嵌套）。
    /// 留空表示沿用预设。
    /// </summary>
    public bool? UrlInline { get; set; }
}

/// <summary>
/// 交付规格解析器：把「模型配置 + 用户模板」合成一个可直接用的 <see cref="VideoDeliverySpec" />。
///
/// 解析顺序：
/// <list type="number">
///   <item>按 <c>preset</c> 取内置预设；未指定则按 endpoint / modelKey 推断；</item>
///   <item>用用户模板里<b>显式写了</b>的字段逐项覆盖预设。</item>
/// </list>
///
/// 之所以坚持「预设 + 覆盖」而不是「全靠用户填」：绝大多数 OpenAI 兼容网关
/// 只需要填 endpoint 就能跑，完整模板只在遇到非标准接口时才需要动笔。
/// </summary>
public static class VideoDeliveryResolver
{
    /// <summary>解析出该模型配置对应的交付规格；返回 null 表示无法推断出可用的形状。</summary>
    public static VideoDeliverySpec? Resolve(ModelConfig model)
    {
        var template = ReadTemplate(model.DeliveryJson);

        var preset = VideoDeliveryPresets.Find(template?.Preset)
                     ?? InferPreset(model);

        if (preset is null)
            return null;

        return ApplyOverrides(preset, template);
    }

    /// <summary>
    /// 按模型配置反推该用哪个预设。
    ///
    /// <b>顺序很关键：先认主机名，再认模型名。</b>
    /// 曾经把百炼的判断放在最前，结果 SiliconFlow 全被判成了百炼——
    /// 因为硅基流动托管的模型名就叫 <c>Wan-AI/Wan2.2-T2V-A14B</c>，含 "wan"。
    /// 也就是说<b>模型名会跨平台重名</b>，主机名才是真正可靠的信号，
    /// 模型名只能当兜底，且必须放在后面。
    /// </summary>
    public static VideoDeliverySpec? InferPreset(ModelConfig model)
    {
        var endpoint = (model.Endpoint ?? string.Empty).ToLowerInvariant();
        var modelKey = (model.ModelKey ?? string.Empty).ToLowerInvariant();

        // 硅基流动：主机名最明确，且它的模型名会与百炼撞车，必须排在百炼前面
        if (endpoint.Contains("siliconflow", StringComparison.Ordinal)
            || endpoint.Contains("silicon", StringComparison.Ordinal))
        {
            return VideoDeliveryPresets.SiliconFlowVideo;
        }

        // 火山方舟：主机名含 volces（火山引擎的根域）或 ark
        if (endpoint.Contains("volces.com", StringComparison.Ordinal)
            || endpoint.Contains("volcengine", StringComparison.Ordinal)
            || endpoint.Contains("ark.cn-", StringComparison.Ordinal))
        {
            return VideoDeliveryPresets.ArkSeedance;
        }

        // 百炼 / DashScope：主机名含 dashscope 或 aliyuncs
        if (endpoint.Contains("dashscope", StringComparison.Ordinal)
            || endpoint.Contains("aliyuncs.com", StringComparison.Ordinal))
        {
            return UsesTierAndRatio(modelKey)
                ? VideoDeliveryPresets.DashScopeVideo
                : VideoDeliveryPresets.DashScopeLegacyVideo;
        }

        // 主机名认不出来时，才退而看模型名（注意放在百炼判断之后）
        if (modelKey.Contains("wan2.2", StringComparison.Ordinal)
            || modelKey.Contains("ltx-video", StringComparison.Ordinal))
        {
            return VideoDeliveryPresets.SiliconFlowVideo;
        }

        // 第三方网关（如 maas.qianwenaiapi.com）主机名认不出百炼，
        // 但模型名是万相——按代际选出正确的参数形态，否则会用 2.5 的 size 去调 3.0
        if (UsesTierAndRatio(modelKey))
            return VideoDeliveryPresets.DashScopeVideo;

        if (modelKey.Contains("wan", StringComparison.Ordinal))
        {
            return VideoDeliveryPresets.DashScopeLegacyVideo;
        }

        // 兜底走 OpenAI 形状：这是「兼容 OpenAI 协议」最常见的对齐方式，
        // 也是让用户只填 endpoint 就能用的关键。
        return VideoDeliveryPresets.OpenAiVideos;
    }

    /// <summary>
    /// 该万相模型是不是走「档位 + 宽高比」的新参数形态。
    ///
    /// 万相 2.5 及更早只有单个 <c>parameters.size</c>（<c>1280*720</c>）；
    /// 2.6 起拆成 <c>parameters.resolution</c>（<c>480P</c>）与 <c>parameters.ratio</c>，
    /// 3.0 沿用新形态。代际判断错一次，请求体就整个不对——
    /// 而接口只会回一句笼统的参数错误，不会告诉你该换哪个字段。
    /// </summary>
    private static bool UsesTierAndRatio(string modelKey)
        => modelKey.Contains("wan2.6", StringComparison.Ordinal)
           || modelKey.Contains("wan2.7", StringComparison.Ordinal)
           || modelKey.Contains("wan3", StringComparison.Ordinal);

    /// <summary>解析模型配置里的交付模板 JSON；格式不对时返回 null 而不是抛异常</summary>
    public static VideoDeliveryTemplate? ReadTemplate(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<VideoDeliveryTemplate>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>把模板序列化成 JSON，用于模型配置的保存</summary>
    public static string SerializeTemplate(VideoDeliveryTemplate? template)
        => template is null ? "{}" : JsonSerializer.Serialize(template, JsonOptions);

    private static VideoDeliverySpec ApplyOverrides(VideoDeliverySpec preset, VideoDeliveryTemplate? template)
    {
        if (template is null)
            return preset;

        // 字段映射即使模板为空也要重建，否则永远拿不到预设的默认值
        var fields = ApplyFieldOverrides(preset.RequestFields, template.RequestFields);

        return new VideoDeliverySpec
        {
            PresetName = string.IsNullOrWhiteSpace(template.Preset) ? preset.PresetName : template.Preset!.Trim(),
            SubmitPath = Coalesce(template.SubmitPath, preset.SubmitPath),
            StatusPath = template.StatusPath is null ? preset.StatusPath : BlankToNull(template.StatusPath),
            StatusMethod = (template.StatusMethod ?? preset.StatusMethod).Trim().ToUpperInvariant(),
            StatusIdField = template.StatusIdField is null ? preset.StatusIdField : BlankToNull(template.StatusIdField),
            TaskIdPath = Coalesce(template.TaskIdPath, preset.TaskIdPath),
            StatusValuePath = Coalesce(template.StatusValuePath, preset.StatusValuePath),
            SucceededStatus = Coalesce(template.SucceededStatus, preset.SucceededStatus),
            FailedStatuses = Coalesce(template.FailedStatuses, preset.FailedStatuses),
            VideoUrlPath = template.VideoUrlPath is null ? preset.VideoUrlPath : BlankToNull(template.VideoUrlPath),
            ErrorPath = Coalesce(template.ErrorPath, preset.ErrorPath),
            OutputKind = ParseOutputKind(template.OutputKind) ?? preset.OutputKind,
            ContentPath = template.ContentPath is null ? preset.ContentPath : BlankToNull(template.ContentPath),
            ExtraHeaders = template.ExtraHeaders is { Count: > 0 }
                ? MergeHeaders(preset.ExtraHeaders, template.ExtraHeaders)
                : preset.ExtraHeaders,
            RequestFields = fields,
            ResolutionStyle = ParseResolutionStyle(template.ResolutionStyle) ?? preset.ResolutionStyle,
            BodyShape = ParseBodyShape(template.BodyShape) ?? preset.BodyShape,
            Capabilities = template.Capabilities ?? preset.Capabilities,
            // 预设的内部属性，不向用户模板开放：路径怎么拼取决于协议本身，不由配置决定
            VendorNativePaths = preset.VendorNativePaths,
        };
    }

    private static VideoRequestFieldMap ApplyFieldOverrides(
        VideoRequestFieldMap preset,
        VideoRequestFieldTemplate? template)
    {
        if (template is null)
            return preset;

        return new VideoRequestFieldMap
        {
            // 统一走「模板写了就用模板的、否则保留预设」的语义。
            // 其中负向提示词等字段模板写空串即表示「这个字段不传」，
            // 与"没配"必须区分得开。
            Model = Override(template.Model, preset.Model),
            Prompt = Override(template.Prompt, preset.Prompt),
            NegativePrompt = Override(template.NegativePrompt, preset.NegativePrompt),
            FirstFrame = Override(template.FirstFrame, preset.FirstFrame),
            Duration = Override(template.Duration, preset.Duration),
            Resolution = Override(template.Resolution, preset.Resolution),
            Ratio = Override(template.Ratio, preset.Ratio),
            Fps = Override(template.Fps, preset.Fps),
            Seed = Override(template.Seed, preset.Seed),
            ExtraParamsContainer = Override(template.ExtraParamsContainer, preset.ExtraParamsContainer) ?? string.Empty,

            // ContentArray 形状专用
            ContentArray = Override(template.ContentArray, preset.ContentArray),
            TextItemType = Override(template.TextItemType, preset.TextItemType),
            ImageItemType = Override(template.ImageItemType, preset.ImageItemType),
            VideoItemType = Override(template.VideoItemType, preset.VideoItemType),
            AudioItemType = Override(template.AudioItemType, preset.AudioItemType),
            UrlField = Override(template.UrlField, preset.UrlField),
            RoleField = Override(template.RoleField, preset.RoleField),
            RoleFirstFrame = Override(template.RoleFirstFrame, preset.RoleFirstFrame),
            RoleLastFrame = Override(template.RoleLastFrame, preset.RoleLastFrame),
            RoleReferenceImage = Override(template.RoleReferenceImage, preset.RoleReferenceImage),
            RoleReferenceVideo = Override(template.RoleReferenceVideo, preset.RoleReferenceVideo),
            RoleReferenceAudio = Override(template.RoleReferenceAudio, preset.RoleReferenceAudio),
            UrlInline = template.UrlInline ?? preset.UrlInline,
        };
    }

    /// <summary>
    /// 模板字段覆盖预设字段的三态语义：
    /// <list type="bullet">
    ///   <item>模板为 <c>null</c>（没配）→ 沿用预设值；</item>
    ///   <item>模板为非空串 → 用模板值；</item>
    ///   <item>模板为空白串 → <c>null</c>，表示「这个字段不传」。</item>
    /// </list>
    /// 第三种是关键：用户能靠它显式关掉预设默认会传的字段。
    /// </summary>
    private static string? Override(string? templateValue, string? presetValue)
        => templateValue is null ? presetValue : BlankToNull(templateValue);

    private static IReadOnlyDictionary<string, string> MergeHeaders(
        IReadOnlyDictionary<string, string> preset,
        Dictionary<string, string> overrides)
    {
        var merged = new Dictionary<string, string>(preset, StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in overrides)
        {
            if (!string.IsNullOrWhiteSpace(key))
                merged[key.Trim()] = value;
        }

        return merged;
    }

    private static VideoOutputKind? ParseOutputKind(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim().ToLowerInvariant() switch
        {
            "url" => VideoOutputKind.Url,
            "content" => VideoOutputKind.ContentBinary,
            "base64" => VideoOutputKind.Base64,
            _ => null,
        };
    }

    private static VideoResolutionStyle? ParseResolutionStyle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim().ToLowerInvariant() switch
        {
            "omit" => VideoResolutionStyle.Omit,
            "pixel" or "pixelsize" or "wxh" => VideoResolutionStyle.PixelSize,
            "tier" => VideoResolutionStyle.Tier,
            "tierratio" or "tier-ratio" or "tier_and_ratio" => VideoResolutionStyle.TierAndRatio,
            // 火山方舟要小写档位：720p
            "tierlower" or "tier-lower" or "lower" => VideoResolutionStyle.TierLower,
            _ => null,
        };
    }

    private static VideoRequestBodyShape? ParseBodyShape(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim().ToLowerInvariant() switch
        {
            "flat" => VideoRequestBodyShape.Flat,
            "contentarray" or "content-array" or "content" => VideoRequestBodyShape.ContentArray,
            _ => null,
        };
    }

    private static string Coalesce(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    /// <summary>把空串归一成 null，用它表达「这个字段不传」</summary>
    private static string? BlankToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}