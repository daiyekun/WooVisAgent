using WooVisAgent.SharedLib.Delivery;
using WooVisAgent.SharedLib.Domain;
using WooVisAgentServices.ImageService.Contracts;

namespace WooVisAgentServices.VideoService.Contracts;

/// <summary>
/// 一次视频生成请求。所有参数都来自任务的参数快照，不依赖数据库里的模型配置，
/// 因此该请求可以安全地由消息重放构造。
///
/// <para>
/// 进度上报用<b>共用</b>的 <see cref="GenerationProgress" /> 而不是视频专属类型：
/// 图片与视频的进度泵（<c>TaskProgressPump</c>）是同一个，
/// 进度类型若各自定义，就要在泵里再做一次类型适配，
/// 而两边形状完全一样，那层适配纯属无谓的转换开销与漂移风险。
/// </para>
/// </summary>
/// <param name="FirstFrame">
/// 首帧图（图生视频）。为 null 时执行文生视频。
/// </param>
/// <param name="Progress">
/// 可选的进度上报通道，由 Worker 传入。不传时只在开始与结束时各写一次状态。
/// </param>
public sealed record VideoGenerationRequest(
    long TaskId,
    string OwnerId,
    string Prompt,
    string? NegativePrompt,
    TaskAsset? FirstFrame,
    string? Resolution,
    int? DurationSeconds,
    int? Fps,
    long? Seed,
    IReadOnlyDictionary<string, object?> ExtraParams,
    ModelConfig Model,
    IProgress<GenerationProgress>? Progress = null,
    /// <summary>输入模式：全能生成 / 首尾帧 / 智能多帧</summary>
    VideoGenerationMode Mode = VideoGenerationMode.Omni,
    /// <summary>
    /// 有序参考素材。元素的 <c>Type</c> 为 image / video / audio。
    /// 为空时按老的单首帧逻辑执行，保证既有视频模型行为不变。
    /// </summary>
    IReadOnlyList<TaskAsset>? References = null)
{
    /// <summary>是否是图生视频</summary>
    public bool IsImageToVideo => FirstFrame is not null && !string.IsNullOrWhiteSpace(FirstFrame.Url);

    /// <summary>
    /// 最终要交给协议层的素材：优先用新的参考素材列表，
    /// 没有时才回落到单一首帧（老任务、老模型）。
    /// </summary>
    public IReadOnlyList<(VideoReferenceKind Kind, string Url)> EffectiveReferences
    {
        get
        {
            if (References is { Count: > 0 } list)
            {
                var mapped = new List<(VideoReferenceKind Kind, string Url)>(list.Count);
                foreach (var asset in list)
                {
                    if (string.IsNullOrWhiteSpace(asset.Url))
                        continue;

                    VideoReferenceKindExtensions.TryParseWireValue(asset.Type, out var kind);
                    mapped.Add((kind, asset.Url));
                }

                if (mapped.Count > 0)
                    return mapped;
            }

            return IsImageToVideo
                ? [(VideoReferenceKind.Image, FirstFrame!.Url)]
                : [];
        }
    }
}

/// <summary>
/// 视频生成服务。AGENT.md 里提到的"视频生成服务"本体：
/// 由 WooVisAgent.GenerateWorker 在消费到队列消息之后调用。
/// </summary>
public interface IVideoGenerationService
{
    /// <summary>
    /// 执行生成并落盘，返回可直接给前端播放的产物列表。
    /// 失败时抛异常，由调用方（Worker 消费者）负责把任务置为失败态。
    /// </summary>
    Task<IReadOnlyList<TaskAsset>> GenerateAsync(
        VideoGenerationRequest request,
        CancellationToken cancellationToken = default);
}