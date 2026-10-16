using WooVisAgent.SharedLib.Domain;

namespace WooVisAgentServices.ImageService.Contracts;

/// <summary>
/// 任务派发器：把已落库的任务参数快照翻译成总线消息并发布到 RabbitMQ。
///
/// 抽出来是为了让"提交"和"重试"两条路径共用同一份派发逻辑，
/// 避免两处各写一遍参数映射而逐渐不一致。
/// </summary>
public interface IGenerationTaskDispatcher
{
    Task DispatchAsync(GenerationTask task, CancellationToken cancellationToken = default);
}
