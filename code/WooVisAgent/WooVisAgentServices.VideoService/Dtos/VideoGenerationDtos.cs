namespace WooVisAgentServices.VideoService.Dtos;

/// <summary>
/// 视频生成入参，字段与前端 WooVisAgent.Web/src/api/video.ts 的
/// <c>VideoGenerationInput</c> 逐项对齐。
///
/// <see cref="ModelId" /> 用字符串而不是数字：雪花 ID 是 64 位，
/// 前端 JS 的 number 存不下会丢精度。
/// </summary>
public sealed class VideoGenerationInput
{
    public string? Name { get; set; }

    public string ModelId { get; set; } = string.Empty;

    public string Prompt { get; set; } = string.Empty;

    public string? NegativePrompt { get; set; }

    /// <summary>
    /// 输入模式：<c>omni</c>（全能，默认）/ <c>firstLastFrame</c>（首尾帧）/ <c>keyframes</c>（智能多帧）。
    /// 留空等同 <c>omni</c>，因此老的前端调用不受影响。
    /// </summary>
    public string? Mode { get; set; }

    /// <summary>
    /// 参考素材，<b>顺序即语义</b>。
    /// 首尾帧取前两张；智能多帧按顺序当关键帧；全能生成按类型分别映射成参考图 / 参考视频 / 参考音频。
    /// </summary>
    public List<VideoReferenceInput>? References { get; set; }

    /// <summary>首帧图地址（图生视频）。填了就按图生视频执行。</summary>
    public string? FirstFrameUrl { get; set; }

    /// <summary>尾帧图地址。部分模型（如百炼 2.7）支持首尾帧生视频。</summary>
    public string? LastFrameUrl { get; set; }

    /// <summary>视频时长，单位秒</summary>
    public int? DurationSeconds { get; set; }

    /// <summary>帧率</summary>
    public int? Fps { get; set; }

    /// <summary>分辨率，如 1920x1080、1280x720</summary>
    public string? Resolution { get; set; }

    /// <summary>是否生成音效</summary>
    public bool? WithAudio { get; set; }

    /// <summary>随机种子</summary>
    public long? Seed { get; set; }

    /// <summary>会话标识；不传则自动新建 / 复用会话</summary>
    public string? ConversationId { get; set; }

    /// <summary>模型扩展参数，键与 ModelParamSpec.key 对应</summary>
    public Dictionary<string, object?>? Params { get; set; }
}

/// <summary>
/// 一份参考素材。<see cref="Kind" /> 取 <c>image</c> / <c>video</c> / <c>audio</c>。
/// </summary>
public sealed class VideoReferenceInput
{
    public string Kind { get; set; } = "image";

    public string Url { get; set; } = string.Empty;
}

/// <summary>
/// 提交视频生成任务的返回。字段与前端 <c>VideoGenerationResult</c> 对齐：
/// 异步架构下产物还没出来，因此 <see cref="TaskId" /> + <see cref="Status" /> 是主体，
/// <see cref="Video" /> 保留给将来的同步兜底场景。
/// </summary>
public sealed record VideoGenerationResultDto(
    string TaskId,
    string Status,
    VideoAssetDto? Video = null);

/// <summary>
/// 视频产物。字段与图片侧的 <c>TaskAssetDto</c> 完全一致——
/// 两者在前端共用同一个 <c>TaskAsset</c> 类型，因此这里不另立一套结构，
/// 免得同一个字段在两边对不上。
/// </summary>
public sealed record VideoAssetDto(
    string Id,
    string Type,
    string Url,
    string? ThumbnailUrl,
    string? FileName,
    long? FileSize,
    int? Width,
    int? Height,
    double? Duration);