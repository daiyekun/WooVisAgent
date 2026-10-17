namespace WooVisAgentServices.ImageService.Contracts;

/// <summary>
/// 生成过程中的一次进度上报（与生成类型无关的统一形状）。
///
/// <para>
/// 抽到这一层是因为进度泵（<c>TaskProgressPump</c>）要同时喂图片与视频两条链路：
/// 泵只认识「百分比 + 阶段文案」，如果图片侧另有自己的进度类型，
/// 视频就得为同一个泵再做一次类型适配，而两边数据形状完全一致，
/// 那层适配纯属无谓的转换开销与漂移风险。
/// </para>
/// </summary>
/// <param name="Percent">0 - 100 的完成度</param>
/// <param name="Stage">可直读的阶段说明，例如「已提交到模型，等待返回」</param>
public sealed record GenerationProgress(int Percent, string? Stage = null)
{
    /// <summary>把百分比夹到合法区间，避免上游算错时把进度条撑爆</summary>
    public int SafePercent => Math.Clamp(Percent, 0, 100);
}