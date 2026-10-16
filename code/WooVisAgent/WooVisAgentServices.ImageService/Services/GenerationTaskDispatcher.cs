using MassTransit;
using Microsoft.Extensions.Logging;
using WooVisAgent.EventBus;
using WooVisAgent.EventBus.Events;
using WooVisAgent.SharedLib.Domain;
using WooVisAgentServices.ImageService.Contracts;
using WooVisAgentServices.ImageService.Serialization;
using WooVisAgentServices.ImageService.Validation;

namespace WooVisAgentServices.ImageService.Services;

/// <summary>
/// 任务派发器实现：把任务参数快照翻译成 <see cref="ImageGenerationRequested" /> 并发布。
///
/// 发布用的是 MassTransit 的 <c>Publish</c>（fanout exchange），
/// 只有订阅了该消息类型的队列才会收到——即只有 WooVisAgent.GenerateWorker。
/// </summary>
public sealed class GenerationTaskDispatcher(
    IPublishEndpoint publishEndpoint,
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
            DateTimeOffset.UtcNow);

        await publishEndpoint.Publish(message, cancellationToken);

        logger.LogInformation(
            "任务 {TaskId} 已投递到队列 {Queue}，生成数量 {BatchSize}",
            task.Id,
            EventBusConstants.GenerateWorkerQueue,
            message.BatchSize);
    }
}
