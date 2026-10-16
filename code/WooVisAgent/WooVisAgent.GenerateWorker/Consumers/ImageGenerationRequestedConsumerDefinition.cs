using MassTransit;
using WooVisAgent.EventBus;

namespace WooVisAgent.GenerateWorker.Consumers;

/// <summary>
/// 生成任务消费者的端点定义。
///
/// <see cref="EndpointName" /> 显式指定队列名为 AGENT.md 里约定的
/// <c>WooVisAgent.GenerateWorker</c>，这样就不再受 AddEventBus 里
/// SetKebabCaseEndpointNameFormatter 的自动命名影响。
/// </summary>
public class ImageGenerationRequestedConsumerDefinition
    : ConsumerDefinition<ImageGenerationRequestedConsumer>
{
    public ImageGenerationRequestedConsumerDefinition()
    {
        EndpointName = EventBusConstants.GenerateWorkerQueue;

        // 单个 Worker 实例最多并行处理 2 个任务，避免把 CPU 和模型接口同时打满
        ConcurrentMessageLimit = 2;
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<ImageGenerationRequestedConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        // 有限次重试，只用于覆盖数据库连接抖动这类瞬时故障。
        // 生成本身的失败已在消费者内部消化为 Failed 状态，不会走到这里，
        // 所以不存在"生成 → 失败 → 重投 → 再生成"的循环。
        endpointConfigurator.UseMessageRetry(retry => retry.Intervals(500, 2000));
    }
}
