using Microsoft.Extensions.Logging;
using Wolverine;
using WooVisAgent.EventBus;
using WooVisAgent.EventBus.Events;
using WooVisAgent.SharedLib.Diagnostics;
using WooVisAgent.SharedLib.Domain;
using WooVisAgentServices.ImageService.Contracts;
using WooVisAgentServices.ImageService.Serialization;
using WooVisAgentServices.ImageService.Validation;

namespace WooVisAgentServices.ImageService.Services;

/// <summary>
/// 任务派发器实现：把任务参数快照翻译成 <see cref="ImageGenerationRequested" /> 并发布。
///
/// 消息按 EventBus 里声明的显式路由投递到 <c>WooVisAgent.GenerateWorker</c> 队列，
/// 只有 WooVisAgent.GenerateWorker 会消费它。
/// </summary>
public sealed class GenerationTaskDispatcher(
    IMessageBus messageBus,
    ILogger<GenerationTaskDispatcher> logger) : IGenerationTaskDispatcher
{
    public async Task DispatchAsync(GenerationTask task, CancellationToken cancellationToken = default)
    {
        if (task.Type != GenerationTaskType.Image)
        {
            logger.LogWarning(
                "任务 {TaskId} 的类型为 {TaskType}，视频生成服务尚未接入，本次不做派发",
                task.Id,
                task.Type);

            return;
        }

        var parameters = GenerationTaskSerialization.DeserializeParams(task.ParamsJson);

        // 链路上下文随消息一起过队列。
        //
        // 这是跨进程追踪唯一可行的做法：RabbitMQ 不会自动传播 Activity，
        // 不显式带上 traceparent，Worker 侧就会开一条全新的链路，
        // Api 的「提交任务」和 Worker 的「实际生成」在面板上变成两条互不相干的记录，
        // 最关键的「从提交到出图总共多久、在哪一步失败」就再也拼不出来了。
        //
        // 放在 PublishAsync 之前采集：PublishAsync 之后 Activity.Current 可能已被
        // Wolverine 的出站埋点换掉，取到的就不再是本次提交的上下文了。
        var traceHeaders = TraceSources.InjectContext();

        var message = new ImageGenerationRequested(
            task.Id,
            task.OwnerId,
            task.Type,
            task.ModelConfigId,
            task.Prompt,
            task.NegativePrompt,
            task.Name,
            GenerationTaskSerialization.ReadInt(parameters, "width", ImageGenerationInputValidator.DefaultWidth),
            GenerationTaskSerialization.ReadInt(parameters, "height", ImageGenerationInputValidator.DefaultHeight),
            GenerationTaskSerialization.ReadInt(parameters, "batchSize", ImageGenerationInputValidator.DefaultBatchSize),
            GenerationTaskSerialization.ReadInt(parameters, "steps", ImageGenerationInputValidator.DefaultSteps),
            GenerationTaskSerialization.ReadDouble(parameters, "cfgScale", ImageGenerationInputValidator.DefaultCfgScale),
            GenerationTaskSerialization.ReadNullableLong(parameters, "seed"),
            task.ParamsJson,
            GenerationTaskSerialization.DeserializeAssets(task.InputsJson),
            DateTimeOffset.UtcNow,
            MessageId: Guid.NewGuid().ToString("N"),
            TraceHeaders: traceHeaders);

        // Wolverine 的 IMessageBus.PublishAsync 不接受 CancellationToken，只有 DeliveryOptions 可传
        await messageBus.PublishAsync(message);
        cancellationToken.ThrowIfCancellationRequested();

        logger.LogInformation(
            "任务 {TaskId} 已投递到队列 {Queue}，生成数量 {BatchSize}",
            task.Id,
            EventBusConstants.GenerateWorkerQueue,
            message.BatchSize);
    }
}
