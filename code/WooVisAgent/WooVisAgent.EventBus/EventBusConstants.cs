namespace WooVisAgent.EventBus;

/// <summary>
/// 事件总线上约定好的名称常量。
/// </summary>
public static class EventBusConstants
{
    /// <summary>
    /// 生成任务队列名。必须是 AGENT.md 中约定的 <c>WooVisAgent.GenerateWorker</c>。
    ///
    /// 由 WooVisAgent.GenerateWorker 侧的 ImageGenerationRequestedConsumerDefinition
    /// 通过 <c>EndpointName</c> 显式指定，因此不会受 SetKebabCaseEndpointNameFormatter 影响。
    /// </summary>
    public const string GenerateWorkerQueue = "WooVisAgent.GenerateWorker";
}
