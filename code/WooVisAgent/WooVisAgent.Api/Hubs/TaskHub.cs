using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace WooVisAgent.Api.Hubs;

/// <summary>
/// 任务实时通道，前端 WooVisAgent.Web/src/hooks/useSignalR.ts 连接的地址是 <c>/hubs/tasks</c>。
///
/// 鉴权：与普通接口共用 JWT。WebSocket 握手带不了请求头，
/// 所以前端通过 <c>access_token</c> 查询参数传递（见 Program 里的 OnMessageReceived）。
///
/// 推送方式：每个连接按用户 ID 加入一个组，Worker 侧的状态变更消息到达 Api 后，
/// 由 <see cref="Consumers.GenerationTaskStatusChangedConsumer" /> 只推给该任务归属用户的组，
/// 实现用户间数据隔离。
/// </summary>
[Authorize]
public class TaskHub : Hub
{
    /// <summary>按用户划分推送组，避免把 A 用户的任务推给 B 用户</summary>
    public static string GroupName(string ownerId) => $"user:{ownerId}";

    public override async Task OnConnectedAsync()
    {
        var userId = Context.UserIdentifier;
        if (!string.IsNullOrEmpty(userId))
            await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(userId));

        await base.OnConnectedAsync();
    }
}
