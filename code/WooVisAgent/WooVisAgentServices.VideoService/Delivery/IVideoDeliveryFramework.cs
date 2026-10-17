using WooVisAgent.SharedLib.Domain;

namespace WooVisAgentServices.VideoService.Delivery;

/// <summary>视频交付框架：一次视频生成的编排入口</summary>
public interface IVideoDeliveryFramework
{
    /// <summary>
    /// 提交 → 轮询 → 下载落盘，返回可供前端播放的产物。
    /// 失败时抛异常，由调用方（Worker 消费者）负责把任务置为失败态。
    /// </summary>
    Task<IReadOnlyList<TaskAsset>> GenerateVideoAsync(
        Contracts.VideoGenerationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>对视频模型配置做连通性测试，用于「模型配置」页面的测试按钮</summary>
    Task<VideoDeliveryProbeResult> TestConnectivityAsync(
        ModelConfig model,
        CancellationToken cancellationToken = default);
}

/// <summary>视频连通性探测结果</summary>
public sealed record VideoDeliveryProbeResult(
    bool Success,
    int? LatencyMilliseconds,
    string? Message,
    string? ResolvedUrl);