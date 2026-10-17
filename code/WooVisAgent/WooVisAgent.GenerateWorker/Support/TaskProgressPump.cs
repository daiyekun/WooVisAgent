using System.Threading.Channels;
using WooVisAgent.SharedLib.Domain;
using WooVisAgent.SharedLib.Repository;
using WooVisAgentServices.ImageService.Contracts;

namespace WooVisAgent.GenerateWorker.Support;

/// <summary>
/// 把生成过程中的进度上报写回任务表并推给前端。
///
/// 为什么需要一个泵（pump）而不是在 <c>IProgress.Report</c> 里直接落库：
/// <list type="bullet">
///   <item><c>IProgress&lt;T&gt;.Report</c> 是同步方法，而落库与推送都是异步的，
///         直接 await 做不到，阻塞等待又会把生成线程拖住；</item>
///   <item>异步模型轮询每 3 秒就上报一次，全部转发会刷出一堆无意义的消息。</item>
/// </list>
/// 因此这里用 channel 收集上报，由后台泵按「进度至少涨 4% 或间隔超过 2 秒」的节奏合并后转发，
/// 既不会阻塞生成，也不会把消息总线刷爆。
/// </summary>
public sealed class TaskProgressPump
{
    /// <summary>进度小于这个幅度、且阶段文案没变时不重复推送</summary>
    private const int MinPercentDelta = 4;

    /// <summary>两次推送的最小间隔，避免轮询阶段刷屏</summary>
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(2);

    private readonly IRepository<GenerationTask> _taskRepository;
    private readonly IGenerationTaskNotifier _notifier;
    private readonly GenerationTask _task;
    private readonly Channel<ImageGenerationProgress> _channel;

    private int _lastPercent;
    private string? _lastStage;
    private DateTimeOffset _lastSentAt = DateTimeOffset.MinValue;

    public TaskProgressPump(
        IRepository<GenerationTask> taskRepository,
        IGenerationTaskNotifier notifier,
        GenerationTask task)
    {
        _taskRepository = taskRepository;
        _notifier = notifier;
        _task = task;
        _lastPercent = task.Progress;

        _channel = Channel.CreateUnbounded<ImageGenerationProgress>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        // 字段初始化器不能引用实例成员，适配器只能在构造函数里接线
        Progress = new ProgressAdapter(_channel.Writer);
    }

    /// <summary>交给 <see cref="ImageGenerationRequest" /> 的上报通道</summary>
    public IProgress<ImageGenerationProgress> Progress { get; }

    /// <summary>跑泵：一直消费到通道被 <see cref="Complete" /> 关闭为止</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // 合并：同一时刻积压的多条上报只取最新的一条，
                // 中间的旧值对用户没有意义，省掉能明显减少数据库写与消息量。
                ImageGenerationProgress? latest = null;
                while (_channel.Reader.TryRead(out var item))
                    latest = item;

                if (latest is null)
                    continue;

                await ApplyAsync(latest, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (ChannelClosedException)
        {
            // 通道被关闭即代表生成已结束，正常退出
        }
        catch (OperationCanceledException)
        {
            // 服务停止
        }
    }

    /// <summary>生成结束后调用：关闭通道，让泵把剩余上报处理完再退出</summary>
    public void Complete() => _channel.Writer.TryComplete();

    private async Task ApplyAsync(ImageGenerationProgress progress, CancellationToken cancellationToken)
    {
        var percent = progress.SafePercent;
        var stage = string.IsNullOrWhiteSpace(progress.Stage) ? null : progress.Stage!.Trim();

        // 百分比不能倒退：多张图出错重试时可能出现回退，进度条往回缩比不动更让人困惑
        if (percent <= _lastPercent && stage == _lastStage)
            return;

        var now = DateTimeOffset.UtcNow;
        if (percent < 100
            && stage == _lastStage
            && percent - _lastPercent < MinPercentDelta
            && now - _lastSentAt < MinInterval)
            return;

        _lastPercent = percent;
        _lastStage = stage;
        _lastSentAt = now;

        _task.Progress = percent;
        _task.UpdatedAt = now;
        _taskRepository.Update(_task);
        await _taskRepository.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _notifier.NotifyAsync(_task, message: stage, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>把 <see cref="IProgress{T}" /> 接到 channel 上</summary>
    private sealed class ProgressAdapter(ChannelWriter<ImageGenerationProgress> writer)
        : IProgress<ImageGenerationProgress>
    {
        public void Report(ImageGenerationProgress value) => writer.TryWrite(value);
    }
}
