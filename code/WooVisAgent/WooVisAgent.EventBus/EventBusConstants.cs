namespace WooVisAgent.EventBus;

/// <summary>
/// 事件总线上约定好的名称常量。
/// </summary>
public static class EventBusConstants
{
    /// <summary>
    /// 生成任务队列名。必须是 AGENT.md 中约定的 <c>WooVisAgent.GenerateWorker</c>。
    ///
    /// 由 AddEventBus 的显式路由绑定，Worker 侧监听该队列。
    /// </summary>
    public const string GenerateWorkerQueue = "WooVisAgent.GenerateWorker";

    /// <summary>
    /// Api 侧队列名：Worker 把任务状态变更发到这里，Api 消费后转成 SignalR 推送。
    /// </summary>
    public const string ApiQueue = "WooVisAgent.Api";
}
