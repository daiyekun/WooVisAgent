using WooVisAgent.SharedLib.Domain;

namespace WooVisAgent.EventBus.Events;

/// <summary>
/// 视频生成任务已提交事件。
///
/// 发布方：WooVisAgent.Api（提交视频生成用例）。
/// 消费方：<b>只有</b> WooVisAgent.GenerateWorker。
///
/// 与 <see cref="ImageGenerationRequested" /> 同构：消息里带完整的参数快照与输入素材，
/// Worker 不必回查模型配置即可执行，因此消息重复投递也能安全重放。
/// </summary>
/// <param name="FirstFrame">
/// 首帧图（图生视频）。为 <c>null</c> 时执行文生视频。
/// 之所以传整份 <see cref="TaskAsset" /> 而不是只传 URL：Worker 要先判断
/// 它是本站 <c>/uploads</c> 文件（读字节转 base64）还是公网地址（直接透传），
/// 这项判断需要文件名等元信息。
/// </param>
/// <param name="MessageId">消息唯一标识，落进 span 的 messaging.message.id</param>
/// <param name="TraceHeaders">
/// W3C traceparent。Api 发布时写入、Worker 消费时还原，两侧才能接成同一条链路。
/// 带默认值是为了兼容队列里可能残留的旧格式消息。
/// </param>
public record VideoGenerationRequested(
    long TaskId,
    string OwnerId,
    GenerationTaskType TaskType,
    long ModelConfigId,
    string Prompt,
    string? NegativePrompt,
    string Name,
    TaskAsset? FirstFrame,
    string? Resolution,
    int? DurationSeconds,
    int? Fps,
    bool WithAudio,
    long? Seed,
    string ParamsJson,
    DateTimeOffset RequestedAt,
    string? MessageId = null,
    Dictionary<string, string>? TraceHeaders = null);