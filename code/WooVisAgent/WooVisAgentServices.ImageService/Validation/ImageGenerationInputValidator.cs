using WooVisAgentServices.ImageService.Dtos;

namespace WooVisAgentServices.ImageService.Validation;

/// <summary>图片生成入参校验，同时给出参数快照的归一化结果</summary>
public static class ImageGenerationInputValidator
{
    public const int MaxPromptLength = 2000;
    public const int MaxNegativePromptLength = 1000;

    public const int DefaultWidth = 1024;
    public const int DefaultHeight = 1024;
    public const int MinSize = 64;
    public const int MaxSize = 4096;

    public const int DefaultBatchSize = 1;
    public const int MaxBatchSize = 8;

    public const int DefaultSteps = 30;
    public const int MaxSteps = 150;

    public const double DefaultCfgScale = 7;
    public const double MaxCfgScale = 30;

    /// <summary>校验失败返回可展示的错误文案，通过则返回 null</summary>
    public static string? Validate(ImageGenerationInput? input)
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

        if (input.Width is { } width && (width < MinSize || width > MaxSize))
            return $"图片宽度需在 {MinSize} - {MaxSize} 之间。";

        if (input.Height is { } height && (height < MinSize || height > MaxSize))
            return $"图片高度需在 {MinSize} - {MaxSize} 之间。";

        if (input.BatchSize is { } batchSize && (batchSize < 1 || batchSize > MaxBatchSize))
            return $"生成数量需在 1 - {MaxBatchSize} 之间。";

        if (input.Steps is { } steps && (steps < 1 || steps > MaxSteps))
            return $"采样步数需在 1 - {MaxSteps} 之间。";

        if (input.CfgScale is { } cfgScale && (cfgScale < 1 || cfgScale > MaxCfgScale))
            return $"提示词强度需在 1 - {MaxCfgScale} 之间。";

        if (input.ReferenceImageUrls is { Count: > 8 })
            return "参考图最多 8 张。";

        return null;
    }

    /// <summary>
    /// 归一化并生成落库用的参数快照。
    /// 快照是 Worker 执行时的唯一依据，因此这里把所有用得到的参数显式固化下来。
    /// </summary>
    public static Dictionary<string, object?> BuildParamsSnapshot(ImageGenerationInput input)
    {
        var snapshot = new Dictionary<string, object?>
        {
            ["width"] = input.Width ?? DefaultWidth,
            ["height"] = input.Height ?? DefaultHeight,
            ["batchSize"] = input.BatchSize ?? DefaultBatchSize,
            ["steps"] = input.Steps ?? DefaultSteps,
            ["cfgScale"] = input.CfgScale ?? DefaultCfgScale,
        };

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

    /// <summary>去掉空白项并做超长保护</summary>
    public static List<string> NormalizeUrls(IEnumerable<string>? urls)
    {
        if (urls is null)
            return [];

        return urls
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => url.Trim())
            .Where(url => url.Length is > 0 and <= 1024)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string? NormalizeOptional(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }
}
