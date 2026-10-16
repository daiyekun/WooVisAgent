using System.Globalization;
using MassTransit;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using WooVisAgent.Api.Hubs;
using WooVisAgent.EventBus.Events;
using WooVisAgent.SharedLib.Domain;
using WooVisAgentServices.ImageService.Dtos;

namespace WooVisAgent.Api.Consumers;

/// <summary>
/// 把 Worker 发布的任务状态变更转成 SignalR 推送，让前端页面实时刷新进度并展示生成出来的图片。
///
/// 这里刻意<b>只做推送，不再往总线发布任何消息</b>。
/// 如果这里把状态又发回队列，就会形成
/// 「Api 发任务 → Worker 生成 → Worker 发状态 → Api 收状态后再发消息 → …」的无限回环，
/// 所以本消费者是整条链路的终点。
/// </summary>
public class GenerationTaskStatusChangedConsumer(
    IHubContext<TaskHub> hubContext,
    ILogger<GenerationTaskStatusChangedConsumer> logger)
    : IConsumer<GenerationTaskStatusChanged>
{
    public async Task Consume(ConsumeContext<GenerationTaskStatusChanged> context)
    {
        var message = context.Message;
        var taskId = message.TaskId.ToString(CultureInfo.InvariantCulture);
        var status = message.Status.ToWireValue();
        var clients = hubContext.Clients.Group(TaskHub.GroupName(message.OwnerId));

        // 1. 状态 / 进度：所有状态变化都推一次，前端据此更新进度条与状态标签
        await clients.SendAsync(
            "TaskStatusChanged",
            new TaskStatusChangedPayload(taskId, status, message.Progress, message.Message),
            context.CancellationToken);

        // 2. 终态：额外推一条语义化事件
        switch (message.Status)
        {
            case GenerationTaskStatus.Succeeded:
                await clients.SendAsync(
                    "TaskCompleted",
                    new TaskCompletedPayload(taskId, message.Outputs?.Select(ToDto).ToList()),
                    context.CancellationToken);
                break;

            case GenerationTaskStatus.Failed:
                await clients.SendAsync(
                    "TaskFailed",
                    new TaskFailedPayload(taskId, message.Message),
                    context.CancellationToken);
                break;
        }

        logger.LogDebug("任务 {TaskId} 状态 {Status} 已推送至用户 {OwnerId}", taskId, status, message.OwnerId);
    }

    private static TaskAssetDto ToDto(TaskAsset asset) => new(
        asset.Id,
        asset.Type,
        asset.Url,
        asset.ThumbnailUrl,
        asset.FileName,
        asset.FileSize,
        asset.Width,
        asset.Height,
        asset.Duration);

    /// <summary>字段名与前端 TaskStatusChanged 事件负载一致（SignalR 默认 camelCase）</summary>
    private sealed record TaskStatusChangedPayload(string TaskId, string Status, int Progress, string? Message);

    private sealed record TaskCompletedPayload(string TaskId, IReadOnlyList<TaskAssetDto>? Outputs);

    private sealed record TaskFailedPayload(string TaskId, string? Message);
}
