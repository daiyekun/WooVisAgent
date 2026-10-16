using WooVisAgent.SharedLib.Domain;

namespace WooVisAgentServices.ImageService.Contracts;

/// <summary>
/// 一次图片生成请求。所有参数都来自任务的参数快照，不依赖数据库里的模型配置，
/// 因此该请求可以安全地由消息重放构造。
/// </summary>
public sealed record ImageGenerationRequest(
    long TaskId,
    string OwnerId,
    string Prompt,
    string? NegativePrompt,
    int Width,
    int Height,
    int BatchSize,
    int Steps,
    double CfgScale,
    long? Seed,
    IReadOnlyList<TaskAsset> ReferenceImages,
    ModelConfig Model);

/// <summary>
/// 图片生成服务。这是 AGENT.md 里提到的"图片生成服务"本体：
/// 由 WooVisAgent.GenerateWorker 在消费到队列消息之后调用。
/// </summary>
public interface IImageGenerationService
{
    /// <summary>
    /// 执行生成并落盘，返回可直接给前端展示的产物列表。
    /// 失败时抛异常，由调用方（Worker 消费者）负责把任务置为失败态。
    /// </summary>
    Task<IReadOnlyList<TaskAsset>> GenerateAsync(
        ImageGenerationRequest request,
        CancellationToken cancellationToken = default);
}
