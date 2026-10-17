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

        return null;
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