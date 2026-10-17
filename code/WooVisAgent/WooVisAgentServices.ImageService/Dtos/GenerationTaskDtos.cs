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

    /// <summary>
    /// 所属会话主键。为空表示这是"无会话"的独立提交（例如从任务列表直接发起），
    /// 不会出现在最近会话里。有值则任务挂到该会话下，聊天历史可完整回放。
    /// </summary>
    public string? ConversationId { get; set; }

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
    string? CreatedBy,
    bool IsSelected,
    /// <summary>所属会话主键；不在任何会话下的独立任务为 null</summary>
    string? ConversationId,
    /// <summary>由哪个任务重试 / 迭代而来</summary>
    string? SourceTaskId);

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

/// <summary>
/// 作品出参，字段与前端 MaterialAsset 对齐。
/// 继承自 TaskAssetDto 是因为作品同样有类型 / 地址 / 缩略图 / 文件名 / 尺寸 / 时长，
/// 差异只在多出的分组、提示词与来源信息。
/// </summary>
public sealed record MaterialAssetDto(
    string Id,
    string Type,
    string Url,
    string? ThumbnailUrl,
    string? FileName,
    long? FileSize,
    int? Width,
    int? Height,
    double? Duration,
    string? GroupName,
    string? Prompt,
    string? SourceTaskId,
    string? SourceTaskName,
    DateTimeOffset CreatedAt);

/// <summary>选秀结果：本次入库的作品条数与任务最新状态</summary>
public sealed record SelectGenerationTaskResultDto(
    int AddedCount,
    GenerationTaskDto Task);

/** ==================== 会话 ==================== */

/// <summary>会话列表项，字段与前端 Conversation 对齐</summary>
public sealed record ConversationDto(
    string Id,
    string Title,
    string Type,
    int MessageCount,
    int TaskCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>会话内的单条消息，字段与前端 ChatMessage 对齐</summary>
public sealed record ConversationMessageDto(
    string Id,
    string Role,
    string Text,
    IReadOnlyList<TaskAssetDto> Assets,
    IReadOnlyDictionary<string, object?> Params,
    /// <summary>关联任务 id；用户消息为空</summary>
    string? TaskId,
    /// <summary>同一次生成里的配对消息 id（图生图时用于标注"改自第几轮"）</summary>
    string? PairMessageId,
    /// <summary>图生图来源任务 id</summary>
    string? SourceTaskId,
    int Seq,
    string? ErrorMessage,
    DateTimeOffset CreatedAt);

/// <summary>
/// 会话详情：会话本身 + 全部消息 + 该会话下的任务。
///
/// 任务单独带出来（而不是只挂在消息上）是因为聊天页要在每张结果卡上
/// 展示尺寸 / 张数 / 模型 / 用时，这些都是任务的属性；
/// 消息里的 AssetsJson 只存地址，够画图但不够画卡。
/// </summary>
public sealed record ConversationDetailDto(
    ConversationDto Conversation,
    IReadOnlyList<ConversationMessageDto> Messages,
    IReadOnlyList<GenerationTaskDto> Tasks);

/// <summary>删除会话的影响面提示，让前端能告诉用户"关联任务也一起删了，但作品保留"</summary>
public sealed record DeleteConversationResultDto(
    int DeletedMessages,
    int DeletedTasks,
    /// <summary>因已选秀而保留在作品库里的产物条数</summary>
    int KeptMaterials);
