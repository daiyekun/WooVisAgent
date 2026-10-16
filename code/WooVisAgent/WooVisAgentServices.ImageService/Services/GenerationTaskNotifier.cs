using MassTransit;
using Microsoft.Extensions.Logging;
using WooVisAgent.EventBus.Events;
using WooVisAgent.SharedLib.Domain;
using WooVisAgentServices.ImageService.Contracts;

namespace WooVisAgentServices.ImageService.Services;

/// <inheritdoc />
public sealed class GenerationTaskNotifier(
    IPublishEndpoint publishEndpoint,
    ILogger<GenerationTaskNotifier> logger) : IGenerationTaskNotifier
{
    public async Task NotifyAsync(
        GenerationTask task,
        IReadOnlyList<TaskAsset>? outputs = null,
        string? message = null,
        CancellationToken cancellationToken = default)
    {
        var notification = new GenerationTaskStatusChanged(
            task.Id,
            task.OwnerId,
            task.Type,
            task.Status,
            task.Progress,
            message ?? task.ErrorMessage,
            outputs?.ToList(),
            DateTimeOffset.UtcNow);

        await publishEndpoint.Publish(notification, cancellationToken);

        logger.LogDebug("任务 {TaskId} 状态通知已发送：{Status} {Progress}%",
            task.Id, task.Status, task.Progress);
    }
}
