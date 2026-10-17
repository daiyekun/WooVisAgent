using System.Globalization;
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
{
    /// <summary>
    /// Wolverine 处理器约定：方法名为 <c>Handle</c>，第一个参数是消息类型。
    /// </summary>
    public async Task Handle(GenerationTaskStatusChanged message, CancellationToken cancellationToken)
    {
        var taskId = message.TaskId.ToString(CultureInfo.InvariantCulture);
        var status = message.Status.ToWireValue();
        var clients = hubContext.Clients.Group(TaskHub.GroupName(message.OwnerId));
        var outputs = message.Outputs?.Select(ToDto).ToList();

        // 1. 状态 / 进度：所有状态变化都推一次，前端据此更新进度条与状态标签。
        //
        // outputs 必须随每一条事件一起带出去，不能只留给下面的 TaskCompleted。
        // 否则前端收到"已完成 + 100%"却拿不到产物，就会把卡片收尾成"100% 但没有图"，
        // 随后到达的 TaskCompleted 又因为订阅已注销而被丢弃 —— 表现就是永远不出图。
        await clients.SendAsync(
            "TaskStatusChanged",
            new TaskStatusChangedPayload(taskId, status, message.Progress, message.Message, outputs),
            cancellationToken);

        // 2. 终态：额外推一条语义化事件
        switch (message.Status)
        {
            case GenerationTaskStatus.Succeeded:
                await clients.SendAsync(
                    "TaskCompleted",
                    new TaskCompletedPayload(taskId, outputs),
                    cancellationToken);
                break;

            case GenerationTaskStatus.Failed:
                await clients.SendAsync(
                    "TaskFailed",
                    new TaskFailedPayload(taskId, message.Message),
                    cancellationToken);
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
    private sealed record TaskStatusChangedPayload(
        string TaskId,
        string Status,
        int Progress,
        string? Message,
        IReadOnlyList<TaskAssetDto>? Outputs);

    private sealed record TaskCompletedPayload(string TaskId, IReadOnlyList<TaskAssetDto>? Outputs);

    private sealed record TaskFailedPayload(string TaskId, string? Message);
}
