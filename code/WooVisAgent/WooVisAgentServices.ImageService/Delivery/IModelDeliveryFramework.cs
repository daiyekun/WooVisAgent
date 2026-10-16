using WooVisAgent.SharedLib.Domain;
using WooVisAgentServices.ImageService.Contracts;

namespace WooVisAgentServices.ImageService.Delivery;

/// <summary>
/// 模型交付框架：统一编排「图片生成」与「连通性测试」两件事。
///
/// 这是本服务面向 Microsoft Agent Framework（MAF）方向的抽象落点：
/// <list type="bullet">
///   <item>MAF 的底层统一抽象是 Microsoft.Extensions.AI 的 <c>IChatClient</c>，
///         这里对齐同一套心智模型——"一个客户端，屏蔽异构模型差异"。</item>
///   <item>但图片生成是<b>异构模型网关</b>场景（OpenAI / 通义万相 / 即梦 / ComfyUI / 自建网关），
///         无法被单一 chat client 覆盖，因此图片生成仍走多协议解析层
///         <see cref="WooVisAgent.SharedLib.Delivery.IModelDeliveryProtocol"/>。</item>
///   <item>保留本接口做"编排入口"，未来接入 MAF 多 Agent / 工具调用时，
///         只需替换这里的实现，不影响上层 Worker / 用例。</item>
/// </list>
/// </summary>
public interface IModelDeliveryFramework
{
    /// <summary>
    /// 通过交付框架执行一次图片生成并落盘，返回可供前端展示的产物列表。
    /// 失败时抛异常，由调用方（Worker 消费者）负责把任务置为失败态。
    /// </summary>
    Task<IReadOnlyList<TaskAsset>> GenerateImageAsync(
        ImageGenerationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 对模型配置做连通性测试，返回可读结果。
    /// 用于「模型配置」页面的"测试连通性"按钮。
    /// </summary>
    Task<ModelDeliveryProbeResult> TestConnectivityAsync(
        ModelConfig model,
        CancellationToken cancellationToken = default);
}

/// <summary>模型交付框架的连通性探测结果</summary>
public sealed record ModelDeliveryProbeResult(
    bool Success,
    int? LatencyMilliseconds,
    string? Message,
    string? ResolvedUrl);
