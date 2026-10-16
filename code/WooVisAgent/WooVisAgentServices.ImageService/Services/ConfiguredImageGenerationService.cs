using WooVisAgent.SharedLib.Domain;
using WooVisAgentServices.ImageService.Contracts;
using WooVisAgentServices.ImageService.Delivery;

namespace WooVisAgentServices.ImageService.Services;

/// <summary>
/// 图片生成服务实现（<see cref="IImageGenerationService"/> 的默认实现）。
///
/// 这里是一个<b>薄委托层</b>：真正的"调用哪个模型、用什么协议、怎么解析返回"全部由
/// <see cref="IModelDeliveryFramework"/>（模型交付框架）承担。
///
/// 这样做的意义：
/// <list type="bullet">
///   <item>Worker 只依赖稳定的 <see cref="IImageGenerationService"/> 契约，不感知交付框架细节；</item>
///   <item>未来接入 Microsoft.Agents.AI 的 AIAgent 时，只需替换 IModelDeliveryFramework 的实现，
///         本类与上层 Worker / 用例零改动。</item>
/// </list>
/// </summary>
public sealed class ConfiguredImageGenerationService(IModelDeliveryFramework deliveryFramework)
    : IImageGenerationService
{
    public Task<IReadOnlyList<TaskAsset>> GenerateAsync(
        ImageGenerationRequest request,
        CancellationToken cancellationToken = default)
        => deliveryFramework.GenerateImageAsync(request, cancellationToken);
}
