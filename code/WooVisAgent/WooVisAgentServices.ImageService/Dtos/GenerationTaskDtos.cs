namespace WooVisAgentServices.ImageService.Dtos;

/// <summary>
/// 图片生成入参，字段与前端 WooVisAgent.Web/src/api/image.ts 的 ImageGenerationInput 对齐。
/// </summary>
public sealed class ImageGenerationInput
{
    /// <summary>不传则由后端按提示词截取生成</summary>
    public string? Name { get; set; }

    /// <summary>模型配置主键（字符串形式传入，避免 JS 丢失 64 位精度）</summary>
    public string ModelId { get; set; } = string.Empty;

    public string Prompt { get; set; } = string.Empty;

    public string? NegativePrompt { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    /// <summary>一次生成的张数</summary>
    public int? BatchSize { get; set; }

    public int? Steps { get; set; }

    public double? CfgScale { get; set; }

    public long? Seed { get; set; }

    /// <summary>参考图 / 垫图 URL</summary>
    public List<string>? ReferenceImageUrls { get; set; }

    /// <summary>模型特有参数，键值对应 ModelParamSpec.key</summary>
    public Dictionary<string, object?>? Params { get; set; }
}

/// <summary>任务素材出参，字段与前端 TaskAsset 对齐</summary>
public sealed record TaskAssetDto(
    string Id,
    string Type,
    string Url,
    string? ThumbnailUrl,
    string? FileName,
    long? FileSize,
    int? Width,
    int? Height,
    double? Duration);

/// <summary>
/// 提交图片生成任务的返回结构，与前端 ImageGenerationResult 对齐。
/// 异步生成场景下 <c>Images</c> 为空数组，产物由 SignalR 推送。
/// </summary>
public sealed record ImageGenerationResultDto(
    string TaskId,
    string Status,
    IReadOnlyList<TaskAssetDto> Images);

/// <summary>生成任务出参，字段与前端 GenerationTask 对齐</summary>
public sealed record GenerationTaskDto(
    string Id,
    string Name,
    string Type,
    string Status,
    int Progress,
    string ModelId,
    string? ModelName,
    string Prompt,
    string? NegativePrompt,
    IReadOnlyDictionary<string, object?> Params,
    IReadOnlyList<TaskAssetDto> Inputs,
    IReadOnlyList<TaskAssetDto> Outputs,
    string? ErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? CreatedBy);

/// <summary>任务状态轻量出参，供前端轮询兜底使用</summary>
public sealed record TaskStatusDto(
    string Id,
    string Status,
    int Progress,
    string? ErrorMessage);

/// <summary>分页结果，与前端 PagedResult 对齐</summary>
public sealed record PagedResultDto<T>(
    IReadOnlyList<T> Items,
    int Total,
    int Page,
    int PageSize);
