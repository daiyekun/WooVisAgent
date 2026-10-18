namespace WooVisAgentServices.AgentService.Dtos;

/// <summary>提示词润色入参</summary>
/// <param name="Prompt">用户原始提示词</param>
/// <param name="Target">目标产物：<c>image</c> 或 <c>video</c>，影响润色侧重点</param>
/// <param name="Style">可选的风格偏好，如「电影感」「二次元」，为空则不干预</param>
public record PromptPolishRequest(string Prompt, string? Target = "image", string? Style = null);

/// <summary>提示词润色出参</summary>
/// <param name="Original">原始输入，便于前端对比展示</param>
/// <param name="Polished">润色后的提示词</param>
/// <param name="Negative">建议的负向提示词，可能为空</param>
/// <param name="Tips">简短建议，最多 3 条</param>
public record PromptPolishDto(
    string Original,
    string Polished,
    string? Negative,
    IReadOnlyList<string> Tips);

/// <summary>视频参数规划入参</summary>
/// <param name="Idea">一句话需求，如「一只猫在雨夜街头跳舞」</param>
/// <param name="Mode">可选：<c>text</c>（文生视频）或 <c>image</c>（图生视频）</param>
public record VideoPlanRequest(string Idea, string? Mode = null);

/// <summary>
/// 视频参数建议。**只有建议，不提交任务**——是否花钱由用户在页面上确认。
/// </summary>
/// <param name="Prompt">可直接提交的提示词</param>
/// <param name="ModelId">建议使用的模型配置 Id（字符串，避免 JS 精度丢失）；未匹配到则为空</param>
/// <param name="ModelName">对应模型展示名，便于前端直接显示</param>
/// <param name="DurationSeconds">建议时长（秒）</param>
/// <param name="Resolution">建议分辨率档位（480P / 720P / 1080P）</param>
/// <param name="Ratio">建议画幅（16:9 / 9:16 / 1:1 / adaptive）</param>
/// <param name="Notes">给用户看的补充说明</param>
public record VideoPlanDto(
    string Prompt,
    string? ModelId,
    string? ModelName,
    int? DurationSeconds,
    string? Resolution,
    string? Ratio,
    IReadOnlyList<string> Notes);

/// <summary>Agent 可用性，供前端决定是否显示相关入口</summary>
/// <param name="Enabled">模块开关是否打开</param>
/// <param name="Available">是否已配置可用的 Llm 模型</param>
/// <param name="ModelName">所用模型配置的展示名</param>
/// <param name="ModelKey">所用模型标识</param>
/// <param name="Reason">不可用时的原因（可直接展示给用户）</param>
public record AgentStatusDto(
    bool Enabled,
    bool Available,
    string? ModelName,
    string? ModelKey,
    string? Reason);
