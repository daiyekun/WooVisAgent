namespace WooVisAgent.SharedLib.Domain;

/// <summary>
/// 生成任务类型。与前端 src/types/index.ts 的 TaskType 一一对应，
/// 对外传输时统一转成小写字符串（image / video）。
/// </summary>
public enum GenerationTaskType
{
    /// <summary>图片生成（文生图 / 图生图）</summary>
    Image = 1,

    /// <summary>视频生成（文生视频 / 图生视频）</summary>
    Video = 2,
}

/// <summary>
/// 生成任务状态。与前端 src/types/index.ts 的 TaskStatus 一一对应，
/// 对外传输时统一转成小写字符串（pending / queued / running / succeeded / failed / canceled）。
/// </summary>
public enum GenerationTaskStatus
{
    /// <summary>已创建但尚未投递到队列</summary>
    Pending = 0,

    /// <summary>已投递到 RabbitMQ，等待 Worker 消费</summary>
    Queued = 1,

    /// <summary>Worker 正在生成</summary>
    Running = 2,

    /// <summary>生成成功，产物已落库</summary>
    Succeeded = 3,

    /// <summary>生成失败，原因见 GenerationTask.ErrorMessage</summary>
    Failed = 4,

    /// <summary>用户主动取消</summary>
    Canceled = 5,
}

/// <summary>
/// 生成任务。主键为 64 位雪花 ID（见 <see cref="Ids.ISnowflakeIdGenerator" />），
/// 任务按用户隔离（<see cref="OwnerId" /> 对应用户的 IdentityUser.Id）。
///
/// 任务的"参数快照"（<see cref="ParamsJson" />）与"素材"（<see cref="InputsJson" />）在提交时一次性写入，
/// Worker 只依赖这份快照执行，不再回查模型配置，因此消息重复投递时具有幂等重放能力。
/// </summary>
public class GenerationTask : BaseEntity<long>, IAggregateRoot, IAggregateRoot<long>
{
    /// <summary>展示名称。用户未填写时由提示词截取生成</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>任务归属用户（IdentityUser.Id），实现用户间数据隔离</summary>
    public string OwnerId { get; set; } = string.Empty;

    public GenerationTaskType Type { get; set; } = GenerationTaskType.Image;

    public GenerationTaskStatus Status { get; set; } = GenerationTaskStatus.Pending;

    /// <summary>0 - 100</summary>
    public int Progress { get; set; }

    /// <summary>提交时使用的模型配置主键</summary>
    public long ModelConfigId { get; set; }

    /// <summary>模型名称快照，避免模型被删除后列表无法展示</summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>供应方快照</summary>
    public string Provider { get; set; } = string.Empty;

    public string Prompt { get; set; } = string.Empty;

    public string? NegativePrompt { get; set; }

    /// <summary>提交时的完整参数快照 JSON（width / height / batchSize / steps / cfgScale / seed 等）</summary>
    public string ParamsJson { get; set; } = "{}";

    /// <summary>输入素材 JSON（<see cref="TaskAsset" /> 数组）</summary>
    public string InputsJson { get; set; } = "[]";

    /// <summary>生成产物 JSON（<see cref="TaskAsset" /> 数组），由 Worker 生成后回写</summary>
    public string OutputsJson { get; set; } = "[]";

    /// <summary>
    /// 是否已被「选秀」收录进我的作品库。
    /// 一旦置位前端就不再允许重复选秀，同时禁用重试——重试会生成新产物，
    /// 与已入库的作品语义冲突，交给用户先在作品库处理。
    /// </summary>
    public bool IsSelected { get; set; }

    /// <summary>
    /// 所属创作会话。为空表示从「任务列表」直接提交的任务（不经过对话页），
    /// 这类任务不出现在任何会话下，删除会话时也不会被牵连。
    /// </summary>
    public long? ConversationId { get; set; }

    /// <summary>
    /// 由哪个任务重试而来。用于在会话里标出"这一轮是上一轮的延续"，
    /// 也让删除会话时可以顺藤摸瓜把重试链一起清掉。
    /// </summary>
    public long? SourceTaskId { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>重试次数，供后续做重试上限控制</summary>
    public int RetryCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>投递到队列的时间</summary>
    public DateTimeOffset? QueuedAt { get; set; }

    /// <summary>Worker 开始执行的时间</summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>进入终态（成功 / 失败 / 取消）的时间</summary>
    public DateTimeOffset? FinishedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public static class GenerationTaskTypeExtensions
{
    /// <summary>转成前端使用的小写类型标识</summary>
    public static string ToWireValue(this GenerationTaskType type) => type switch
    {
        GenerationTaskType.Image => "image",
        GenerationTaskType.Video => "video",
        _ => "image",
    };

    /// <summary>解析前端传入的类型标识，忽略大小写</summary>
    public static bool TryParseWireValue(string? value, out GenerationTaskType type)
    {
        type = GenerationTaskType.Image;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        switch (value.Trim().ToLowerInvariant())
        {
            case "image":
                type = GenerationTaskType.Image;
                return true;
            case "video":
                type = GenerationTaskType.Video;
                return true;
            default:
                return false;
        }
    }
}

public static class GenerationTaskStatusExtensions
{
    /// <summary>转成前端使用的小写状态标识</summary>
    public static string ToWireValue(this GenerationTaskStatus status) => status switch
    {
        GenerationTaskStatus.Pending => "pending",
        GenerationTaskStatus.Queued => "queued",
        GenerationTaskStatus.Running => "running",
        GenerationTaskStatus.Succeeded => "succeeded",
        GenerationTaskStatus.Failed => "failed",
        GenerationTaskStatus.Canceled => "canceled",
        _ => "pending",
    };

    /// <summary>解析前端传入的状态标识，忽略大小写</summary>
    public static bool TryParseWireValue(string? value, out GenerationTaskStatus status)
    {
        status = GenerationTaskStatus.Pending;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        switch (value.Trim().ToLowerInvariant())
        {
            case "pending":
                status = GenerationTaskStatus.Pending;
                return true;
            case "queued":
                status = GenerationTaskStatus.Queued;
                return true;
            case "running":
                status = GenerationTaskStatus.Running;
                return true;
            case "succeeded":
                status = GenerationTaskStatus.Succeeded;
                return true;
            case "failed":
                status = GenerationTaskStatus.Failed;
                return true;
            case "canceled":
                status = GenerationTaskStatus.Canceled;
                return true;
            default:
                return false;
        }
    }

    /// <summary>是否已进入终态（终态任务不可再次执行）</summary>
    public static bool IsFinal(this GenerationTaskStatus status) => status
        is GenerationTaskStatus.Succeeded
        or GenerationTaskStatus.Failed
        or GenerationTaskStatus.Canceled;
}
