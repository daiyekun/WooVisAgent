using Microsoft.Extensions.Logging;
using Wolverine;
using WooVisAgent.EventBus.Events;
using WooVisAgent.SharedLib.Domain;
using WooVisAgentServices.ImageService.Contracts;

namespace WooVisAgentServices.ImageService.Services;

/// <inheritdoc />
public sealed class GenerationTaskNotifier(
    IMessageBus messageBus,
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

        // Wolverine 的 IMessageBus.PublishAsync 不接受 CancellationToken（只有 DeliveryOptions），
// 所以这里的 cancellationToken 仅用于日志说明，投递本身由 broker 确认。
await messageBus.PublishAsync(notification);
cancellationToken.ThrowIfCancellationRequested();

        logger.LogDebug("任务 {TaskId} 状态通知已发送：{Status} {Progress}%",
            task.Id, task.Status, task.Progress);
    }
}
