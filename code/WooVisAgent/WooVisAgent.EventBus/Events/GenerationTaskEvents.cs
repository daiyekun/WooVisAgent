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
/// <param name="MessageId">
/// 消息唯一标识，落进 span 的 messaging.message.id，面板上据此定位单条消息。
/// </param>
/// <param name="TraceHeaders">
/// <b>链路上下文（W3C traceparent）。</b>Api 发布时由
/// <c>TraceSources.InjectContext()</c> 写入，Worker 消费时用
/// <c>TraceSources.ExtractContext()</c> 还原，两侧才能接成同一条链路。
/// 缺了它，Api 的「提交任务」和 Worker 的「执行生成」会是两条无关的记录，
/// 排查时看不出「这次生成到底花了多久、在哪一步失败」。
/// 带默认值是为了兼容队列里可能残留的旧格式消息——它们没有这个字段，
/// 此时消费端会开一条新链路，而不是解析失败把消息丢掉。
/// </param>
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
    DateTimeOffset RequestedAt,
    string? MessageId = null,
    Dictionary<string, string>? TraceHeaders = null);

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
