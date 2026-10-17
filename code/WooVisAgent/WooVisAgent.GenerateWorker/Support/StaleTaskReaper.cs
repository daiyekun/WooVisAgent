using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WooVisAgent.SharedLib.Domain;
using WooVisAgent.SharedLib.Repository;
using WooVisAgentServices.ImageService.Contracts;
using WooVisAgentServices.VideoService.Delivery;

namespace WooVisAgent.GenerateWorker.Support;

/// <summary>
/// 僵尸任务清扫器：把「状态停在 Running / Queued 却再也不动」的任务收敛成 Failed。
///
/// <para>
/// 为什么需要它：任务的执行体和它的状态是<b>两回事</b>。
/// Worker 崩了、进程被杀、写库失败、RabbitMQ 消息丢了……
/// 任何一种都会让执行体消失，而数据库里的状态还写着 Running。
/// 更糟的是消费者里的幂等保护看到 Running 就跳过后续重投，
/// 于是这条任务既不会继续，也不会失败——用户只能对着一条永远转圈的进度条干等。
/// </para>
///
/// <para>
/// 它是最后一道兜底，不是主力：正常情况下任务由消费者自己收敛到 Succeeded / Failed，
/// 这里扫到的每一条都意味着前面某一环出了问题，所以一律记 Warning 便于追查。
/// </para>
/// </summary>
public sealed class StaleTaskReaper(
    IServiceScopeFactory scopeFactory,
    ILogger<StaleTaskReaper> logger) : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMinutes(2);

    /// <summary>
    /// 视频任务的僵尸阈值。
    ///
    /// 直接由 <see cref="VideoDeliveryFramework.MaxPollMinutes" /> 推导而不是写死数字：
    /// 轮询窗口一旦放宽，阈值必须跟着放宽，写死就一定会漏改，
    /// 漏改的后果是"正常排队的长任务被当成僵尸误杀"——视频重跑一次是真金白银。
    /// </summary>
    private static readonly TimeSpan VideoThreshold =
        TimeSpan.FromMinutes(VideoDeliveryFramework.MaxPollMinutes + 5);

    /// <summary>图片任务的僵尸阈值。图片轮询窗口是 3 分钟，10 分钟留足余量。</summary>
    private static readonly TimeSpan ImageThreshold = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动先缓一缓：此时 Api / Worker 可能还在建表、初始化数据库，
        // 立刻扫会撞上这些操作，白记一堆错误。
        if (!await DelayAsync(TimeSpan.FromSeconds(30), stoppingToken))
            return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // 扫描失败绝不能让后台服务退出，否则兜底本身没了
                logger.LogError(exception, "僵尸任务扫描失败，将在下个周期重试");
            }

            if (!await DelayAsync(ScanInterval, stoppingToken))
                return;
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();

        var repository = scope.ServiceProvider.GetRequiredService<IRepository<GenerationTask>>();
        var notifier = scope.ServiceProvider.GetRequiredService<IGenerationTaskNotifier>();

        var now = DateTimeOffset.UtcNow;
        var videoCutoff = now - VideoThreshold;
        var imageCutoff = now - ImageThreshold;

        var stale = await repository.GetListAsync(
            task =>
                (task.Status == GenerationTaskStatus.Running
                 || task.Status == GenerationTaskStatus.Queued
                 || task.Status == GenerationTaskStatus.Pending)
                && (task.Type == GenerationTaskType.Video
                    ? task.UpdatedAt < videoCutoff
                    : task.UpdatedAt < imageCutoff),
            cancellationToken).ConfigureAwait(false);

        if (stale.Count == 0)
            return;

        foreach (var task in stale)
        {
            var silentFor = now - task.UpdatedAt;

            // 必须先按<b>改之前</b>的状态生成原因：Running 与 Queued 的处置建议完全不同，
            // 先赋值 Failed 再生成，两种会一起落到兜底分支，用户就只看到一句无用的"任务停滞"。
            var reason = BuildReason(task.Status, silentFor);

            task.Status = GenerationTaskStatus.Failed;
            task.Progress = 100;
            task.FinishedAt = now;
            task.UpdatedAt = now;
            task.ErrorMessage = reason;

            repository.Update(task);

            logger.LogWarning(
                "任务 {TaskId}（{Type}）停留在 {Status} 已 {Minutes:F0} 分钟，判定为僵尸任务并置为失败：{Reason}",
                task.Id,
                task.Type,
                task.Status,
                silentFor.TotalMinutes,
                reason);
        }

        await repository.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // 逐个推送：让用户不必刷新页面就能看到任务终于有了结果
        foreach (var task in stale)
        {
            try
            {
                await notifier.NotifyAsync(task, message: task.ErrorMessage, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogDebug(exception, "任务 {TaskId} 的失败通知推送失败，状态已落库，忽略", task.Id);
            }
        }
    }

    /// <summary>
    /// 按卡住时的状态给出不同的解释，用户才能知道下一步该做什么。
    ///
    /// 传状态而不是传实体：调用方紧接着就会把状态改成 Failed，
    /// 传实体的话这里读到的永远是 Failed，两种原因会一起落到兜底分支。
    /// </summary>
    private static string BuildReason(GenerationTaskStatus stuckAt, TimeSpan silentFor)
    {
        var minutes = silentFor.TotalMinutes.ToString("F0");

        return stuckAt switch
        {
            GenerationTaskStatus.Running =>
                $"生成中断：任务持续 {minutes} 分钟没有任何进展（既未成功也未失败），已判定为失败。" +
                "常见原因是生成服务在生成过程中被重启，或写回状态时出错。" +
                "请直接重跑；若反复出现，检查生成服务的日志。",

            GenerationTaskStatus.Queued or GenerationTaskStatus.Pending =>
                $"任务排队 {minutes} 分钟仍未被生成服务领取，已判定为失败。" +
                "常见原因是生成服务未启动，或消息队列连接异常。请确认生成服务在运行后重跑。",

            _ => $"任务停滞 {minutes} 分钟，已判定为失败，请重跑。",
        };
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
