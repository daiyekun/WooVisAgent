using WooVisAgent.SharedLib.Domain;

namespace WooVisAgentServices.ImageService.Contracts;

/// <summary>
/// 任务状态通知器：把任务当前状态发布到消息总线。
///
/// 这是一条<b>单向通知</b>——发布之后不再有其他副作用。
/// 订阅方（WooVisAgent.Api）只负责把状态转成 SignalR 推送，不会再发消息回总线，
/// 因此不会出现 Api ↔ Worker 的消息回环。
/// </summary>
public interface IGenerationTaskNotifier
{
    Task NotifyAsync(
        GenerationTask task,
        IReadOnlyList<TaskAsset>? outputs = null,
        string? message = null,
        CancellationToken cancellationToken = default);
}
