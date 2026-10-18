using WooVisAgent.SharedLib.Delivery;
using WooVisAgent.SharedLib.Domain;
using WooVisAgentServices.VideoService.Dtos;

namespace WooVisAgentServices.VideoService.Validation;

/// <summary>
/// 视频生成入参校验，同时给出参数快照的归一化结果。
///
/// 与图片侧的 <c>ImageGenerationInputValidator</c> 保持同构，
/// 这样两条链路读起来是同一套心智模型。
/// </summary>
public static class VideoGenerationInputValidator
{
    /// <summary>
    /// 提示词长度上限。
    ///
    /// 取 4000 而不是图片侧的 2000：视频模型的提示词通常要写分镜、镜头运动，
    /// 百炼万相 2.7 的上限是 5000，留一点余量。
    /// </summary>
    public const int MaxPromptLength = 4000;

    public const int MaxNegativePromptLength = 1000;

    /// <summary>时长上限。百炼万相 2.7 最长 15 秒，超过会被直接拒绝。</summary>
    public const int MinDurationSeconds = 1;
    public const int MaxDurationSeconds = 60;

    public const int MinFps = 1;
    public const int MaxFps = 120;

    /// <summary>校验失败返回可展示的错误文案，通过则返回 null</summary>
    public static string? Validate(VideoGenerationInput? input)
    {
        if (input is null)
            return "请求内容不能为空。";

        if (string.IsNullOrWhiteSpace(input.ModelId))
            return "请选择模型。";

        if (!long.TryParse(input.ModelId.Trim(), out _))
            return "模型标识不合法。";

        if (string.IsNullOrWhiteSpace(input.Prompt))
            return "请填写提示词。";

        if (input.Prompt.Trim().Length > MaxPromptLength)
            return $"提示词不能超过 {MaxPromptLength} 个字符。";

        if (input.NegativePrompt is { Length: > MaxNegativePromptLength })
            return $"负向提示词不能超过 {MaxNegativePromptLength} 个字符。";

        if (input.DurationSeconds is { } duration
            && (duration < MinDurationSeconds || duration > MaxDurationSeconds))
        {
            return $"视频时长需在 {MinDurationSeconds} - {MaxDurationSeconds} 秒之间。";
        }

        if (input.Fps is { } fps && (fps < MinFps || fps > MaxFps))
            return $"帧率需在 {MinFps} - {MaxFps} 之间。";

        if (input.FirstFrameUrl is { Length: > 1024 })
            return "首帧图地址过长。";

        if (input.References is { Count: > 0 } references)
        {
            if (references.Count > MaxReferences)
                return $"参考素材最多 {MaxReferences} 个，当前传了 {references.Count} 个。";

            for (var index = 0; index < references.Count; index++)
            {
                var reference = references[index];
                if (reference is null || string.IsNullOrWhiteSpace(reference.Url))
                    return $"第 {index + 1} 个参考素材缺少地址。";

                if (reference.Url.Trim().Length > 2048)
                    return $"第 {index + 1} 个参考素材地址过长。";

                if (!VideoReferenceKindExtensions.TryParseWireValue(reference.Kind, out _))
                    return $"第 {index + 1} 个参考素材的类型只能是 image、video 或 audio。";
            }
        }

        return null;
    }

    /// <summary>参考素材总量上限。真实上限由模型能力决定，这里只挡明显的误操作</summary>
    public const int MaxReferences = 50;

    /// <summary>把入参里的模式 + 素材归一化成一份可直接落库、有序的素材列表</summary>
    public static VideoGenerationMode ResolveMode(VideoGenerationInput input)
        => VideoGenerationModeExtensions.ParseWireValue(input.Mode);

    /// <summary>
    /// 归一化参考素材：把老的 <c>firstFrameUrl</c> 也并进来。
    ///
    /// 老的表单只有一个首帧上传位，它等价于"全能模式下的一张参考图"，
    /// 因此不要求前端立刻改造就能继续工作。
    /// </summary>
    public static List<(VideoReferenceKind Kind, string Url)> ResolveReferences(VideoGenerationInput input)
    {
        var result = new List<(VideoReferenceKind Kind, string Url)>();

        if (input.References is { Count: > 0 })
        {
            foreach (var reference in input.References)
            {
                if (string.IsNullOrWhiteSpace(reference.Url))
                    continue;

                VideoReferenceKindExtensions.TryParseWireValue(reference.Kind, out var kind);
                result.Add((kind, reference.Url.Trim()));
            }
        }

        // 老字段兜底：前端没传 references 但传了 firstFrameUrl 时，按一张参考图处理
        if (result.Count == 0
            && !string.IsNullOrWhiteSpace(input.FirstFrameUrl)
            && ResolveMode(input) == VideoGenerationMode.Omni)
        {
            result.Add((VideoReferenceKind.Image, input.FirstFrameUrl.Trim()));
        }

        return result;
    }

    /// <summary>
    /// 校验「模式 + 素材」与模型声明能力是否匹配。
    ///
    /// <para>三条硬规则都来自火山方舟官方文档，踩中任意一条都会在<b>异步</b>阶段才报错，
    /// 用户看到的现象是"提交成功、等几分钟、然后失败"，极难定位，所以必须在提交前拦下来：</para>
    /// <list type="number">
    ///   <item>首帧 / 首尾帧 / 全模态参考三种场景<b>互斥</b>，不能混着传；</item>
    ///   <item>首尾帧必须恰好两张图，多一张少一张都不行；</item>
    ///   <item>各类素材数量不能超过模型声明的上限。</item>
    /// </list>
    /// </summary>
    public static string? ValidateModeAndCapabilities(
        VideoGenerationInput input,
        ModelConfig model,
        VideoCapabilitySpec? capabilities)
    {
        var mode = ResolveMode(input);
        var references = ResolveReferences(input);

        // 模型没声明能力（老配置）时按"只支持全能、1 张图"的保守口径处理
        capabilities ??= new VideoCapabilitySpec();

        if (!capabilities.Supports(mode))
            return $"模型「{model.Name}」不支持「{mode.ToDisplayName()}」模式，" +
                   $"当前支持的模式：{DescribeModes(capabilities)}。请换一个模型或改用支持的模式。";

        switch (mode)
        {
            case VideoGenerationMode.FirstLastFrame:
                if (references.Count != 2)
                    return $"首尾帧模式需要恰好两张图片（第一张是开头、第二张是结尾），当前是 {references.Count} 张。";

                if (references.Any(x => x.Kind != VideoReferenceKind.Image))
                    return "首尾帧模式只支持图片，不支持视频或音频参考。";

                break;

            case VideoGenerationMode.Keyframes:
                if (references.Count < 2)
                    return $"智能多帧至少需要 2 张关键帧，当前只有 {references.Count} 张。";

                if (references.Count > capabilities.MaxKeyframes)
                    return $"智能多帧最多支持 {capabilities.MaxKeyframes} 张关键帧，当前传了 {references.Count} 张。";

                if (references.Any(x => x.Kind != VideoReferenceKind.Image))
                    return "智能多帧只支持图片作为关键帧。";

                break;

            case VideoGenerationMode.Omni:
                if (references.Any(x => x.Kind != VideoReferenceKind.Image) && !model.SupportsImageToVideo)
                {
                    // 仅图生视频的老模型收不了非图片素材，给一句能看懂的提示而不是笼统的"不支持"
                    var kinds = string.Join("、", references
                        .Select(x => x.Kind.ToReferenceLabel())
                        .Distinct());
                    return $"模型「{model.Name}」只支持图片参考，当前传入了{kinds}。";
                }

                foreach (var kind in new[] { VideoReferenceKind.Image, VideoReferenceKind.Video, VideoReferenceKind.Audio })
                {
                    var count = references.Count(x => x.Kind == kind);
                    if (count == 0)
                        continue;

                    var limit = capabilities.MaxFor(kind);
                    if (limit <= 0)
                        return $"模型「{model.Name}」不支持{kind.ToReferenceLabel()}参考素材。";

                    if (count > limit)
                        return $"{kind.ToReferenceLabel()}参考素材最多 {limit} 个，当前传了 {count} 个。";
                }

                break;
        }

        return null;
    }

    private static string DescribeModes(VideoCapabilitySpec capabilities)
    {
        var names = capabilities.Modes.Select(x => x.ToDisplayName());
        return string.Join("、", names);
    }

    /// <summary>
    /// 按模型声明的能力校验输入形态。
    ///
    /// 这一步不能省：很多视频模型只支持其中一种（例如 <c>wanx2.1-i2v-turbo</c> 只能图生视频）。
    /// 若不校验就放行，用户会得到一个「提交成功但提示词被无视」的结果——
    /// 对图生视频模型做文生视频，产出的画面和提示词毫无关系，
    /// 用户完全无从判断是模型不行还是没接上。
    /// </summary>
    public static string? ValidateCapabilities(VideoGenerationInput input, ModelConfig model, bool hasFirstFrame)
    {
        if (hasFirstFrame && !model.SupportsImageToVideo)
        {
            return $"模型「{model.Name}」不支持图生视频（首帧图），请改用文生视频或换一个支持图生视频的模型。";
        }

        if (!hasFirstFrame && !model.SupportsTextToVideo)
        {
            return $"模型「{model.Name}」不支持文生视频，请提供首帧图按图生视频执行，或换一个支持文生视频的模型。";
        }

        return null;
    }

    /// <summary>
    /// 归一化并生成落库用的参数快照。
    /// 快照是 Worker 执行时的唯一依据，因此这里把所有用得到的参数显式固化下来。
    /// </summary>
    public static Dictionary<string, object?> BuildParamsSnapshot(VideoGenerationInput input)
    {
        var snapshot = new Dictionary<string, object?>();

        // 输入模式必须固化：Worker 重放消息时靠它决定素材怎么映射成 role
        snapshot["mode"] = ResolveMode(input).ToWireValue();

        if (input.Resolution is { Length: > 0 } resolution)
            snapshot["resolution"] = resolution.Trim();

        if (input.DurationSeconds is > 0)
            snapshot["durationSeconds"] = input.DurationSeconds.Value;

        if (input.Fps is > 0)
            snapshot["fps"] = input.Fps.Value;

        if (input.WithAudio is { } withAudio)
            snapshot["withAudio"] = withAudio;

        // 种子为 0 或未填时视为随机，不写入快照
        if (input.Seed is > 0)
            snapshot["seed"] = input.Seed.Value;

        // 模型特有参数原样透传，键与 ModelParamSpec.key 对应
        if (input.Params is { Count: > 0 })
        {
            foreach (var (key, value) in input.Params)
            {
                if (string.IsNullOrWhiteSpace(key) || snapshot.ContainsKey(key))
                    continue;

                snapshot[key] = value;
            }
        }

        return snapshot;
    }

    public static string? NormalizeOptional(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }

    public static string? NormalizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var trimmed = url.Trim();
        return trimmed.Length > 1024 ? null : trimmed;
    }
}