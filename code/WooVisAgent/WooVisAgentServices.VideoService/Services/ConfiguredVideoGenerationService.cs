using Microsoft.Extensions.Logging;
using WooVisAgent.SharedLib.Domain;
using WooVisAgentServices.VideoService.Contracts;
using WooVisAgentServices.VideoService.Delivery;

namespace WooVisAgentServices.VideoService.Services;

/// <summary>
/// 视频生成服务实现。这是一个<b>薄委托</b>：
/// Worker 只认 <see cref="IVideoGenerationService" /> 这一个稳定契约，
/// 真正的编排（提交 / 轮询 / 下载 / 封面）都交给 <see cref="IVideoDeliveryFramework" />。
///
/// 之所以单独隔一层而不是让 Worker 直接依赖框架：
/// Worker 不该知道「提交要拼什么请求体」「产物怎么取」这些接口细节，
/// 将来要接入 MAF 之类的编排时，替换的也只是这层实现，Worker 一行不用改。
/// </summary>
public sealed class ConfiguredVideoGenerationService(
    IVideoDeliveryFramework deliveryFramework,
    ILogger<ConfiguredVideoGenerationService> logger) : IVideoGenerationService
{
    public async Task<IReadOnlyList<TaskAsset>> GenerateAsync(
        VideoGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "开始执行视频生成：任务 {TaskId}，模型 {ModelKey}，模式 {Mode}",
            request.TaskId,
            request.Model.ModelKey ?? request.Model.Name,
            request.IsImageToVideo ? "图生视频" : "文生视频");

        var outputs = await deliveryFramework.GenerateVideoAsync(request, cancellationToken);

        logger.LogInformation(
            "视频生成完成：任务 {TaskId}，产出 {Count} 个文件",
            request.TaskId,
            outputs.Count);

        return outputs;
    }
}