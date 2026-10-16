using WooVisAgent.SharedLib.Domain;

namespace WooVisAgent.EventBus.Events;

/// <summary>
/// 生成任务已提交事件。
///
/// 发布方：WooVisAgent.Api（提交任务用例）。
/// 消费方：<b>只有</b> WooVisAgent.GenerateWorker。
///
/// 消息里携带了完整的参数快照（<paramref name="ParamsJson" />、<paramref name="ReferenceImages" />），
/// Worker 不需要回查模型配置即可执行，因此消息重复投递也能安全重放。
/// </summary>
public record ImageGenerationRequested(
    long TaskId,
    string OwnerId,
    GenerationTaskType TaskType,
    long ModelConfigId,
    string Prompt,
    string? NegativePrompt,
    string Name,
    int Width,
    int Height,
    int BatchSize,
    int Steps,
    double CfgScale,
    long? Seed,
    string ParamsJson,
    List<TaskAsset> ReferenceImages,
    DateTimeOffset RequestedAt);

/// <summary>
/// 生成任务状态已变更事件。
///
/// 发布方：WooVisAgent.GenerateWorker（任务状态落库之后）。
/// 消费方：<b>只有</b> WooVisAgent.Api（把状态推给前端 SignalR）。
///
/// 这是一条"单向通知"，Api 侧的消费者不会再向总线发布任何消息，
/// 因此不存在 Api → Worker → Api 的回环。
/// </summary>
public record GenerationTaskStatusChanged(
    long TaskId,
    string OwnerId,
    GenerationTaskType TaskType,
    GenerationTaskStatus Status,
    int Progress,
    string? Message,
    List<TaskAsset>? Outputs,
    DateTimeOffset OccurredAt);
