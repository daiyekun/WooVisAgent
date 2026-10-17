using WooVisAgent.SharedLib.Domain;

namespace WooVisAgentServices.ImageService.Contracts;

/// <summary>
/// 生成过程中的一次进度上报。
///
/// 以前整条任务只有「5%（开始）」和「100%（结束）」两个进度点，
/// 模型调用动辄几十秒，页面上的进度条就一直不动，用户只能干等，
/// 看起来像卡死。这里把生成拆成几个真实阶段，让进度和文案都能往前走。
/// </summary>
/// <param name="Percent">0 - 100 的完成度</param>
/// <param name="Stage">可直读的阶段说明，例如「已提交到模型，等待返回」</param>
public sealed record ImageGenerationProgress(int Percent, string? Stage = null)
{
    /// <summary>把百分比夹到合法区间，避免上游算错时把进度条撑爆</summary>
    public int SafePercent => Math.Clamp(Percent, 0, 100);
}

/// <summary>
/// 一次图片生成请求。所有参数都来自任务的参数快照，不依赖数据库里的模型配置，
/// 因此该请求可以安全地由消息重放构造。
/// </summary>
/// <param name="Progress">
/// 可选的进度上报通道。由 Worker 传入，用于把「提交 / 等待模型 / 落盘」等阶段实时写回任务表并推送给前端。
/// 不传时完全等同于旧行为（只在开始和结束时各写一次状态）。
/// </param>
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
    ModelConfig Model,
    IProgress<ImageGenerationProgress>? Progress = null);

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
