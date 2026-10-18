using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WooVisAgent.EventBus.Events;
using WooVisAgent.GenerateWorker.Support;
using WooVisAgent.SharedLib.Diagnostics;
using WooVisAgent.SharedLib.Domain;
using WooVisAgent.SharedLib.Repository;
using WooVisAgentServices.ImageService.Contracts;
using WooVisAgentServices.ImageService.Serialization;
using WooVisAgentServices.VideoService.Contracts;
using WooVisAgentServices.VideoService.Delivery;

namespace WooVisAgent.GenerateWorker.Consumers;

/// <summary>
/// 视频生成任务消费者：从队列 <c>WooVisAgent.GenerateWorker</c> 取任务，
/// 调用视频生成服务生成，再把状态写回数据库。
///
/// 状态流转：Queued →（置 Running）→ 生成 → Succeeded / Failed。
///
/// 与图片消费者完全同构的三个关键设计：
/// <list type="number">
///   <item><b>幂等</b>：任务已是成功或已取消时直接跳过，消息重复投递不会重复生成、重复扣费
///         （视频一次生成成本远高于图片，重复扣费的痛也更明显）；</item>
///   <item><b>异常不外抛</b>：失败在本方法内被捕获并落库为 Failed，
///         否则消息会被反复重投，形成"生成 → 失败 → 重投"的死循环；</item>
///   <item><b>产物与状态同一次 SaveChanges</b>：保证前端收到"已完成"时视频已可读。</item>
/// </list>
/// </summary>
public class VideoGenerationRequestedConsumer(
    IRepository<GenerationTask> taskRepository,
    IRepository<ModelConfig> modelRepository,
    IVideoGenerationService videoGenerationService,
    IGenerationTaskNotifier notifier,
    IHostApplicationLifetime hostLifetime,
    ILogger<VideoGenerationRequestedConsumer> logger)
{
    /// <summary>
    /// 判定「状态是 Running、但实际已经死了」的心跳阈值。
    ///
    /// 必须<b>大于</b> <see cref="VideoDeliveryFramework.MaxPollMinutes" />（20 分钟）：
    /// 视频正常生成要 1-5 分钟、排队久的接近窗口上限，阈值给得比窗口小
    /// 会把正在正常出片的任务误判成僵尸，白白重跑一次（视频重跑是真金白银）。
    ///
    /// 反过来，没有这个阈值就是这次故障的放大器：执行体一旦死掉，
    /// 状态永远停在 Running，之后每一次重投都被"已在生成中"跳过，
    /// 任务再也不会有任何进展，也不会失败——用户只看到一条永远转圈的进度条。
    /// </summary>
    /// 由 <see cref="VideoDeliveryFramework.MaxPollMinutes" /> 推导而不是写死，
    /// 避免轮询窗口放宽后这里漏改，把正常排队的长任务误判成僵尸。
    /// </summary>
    private static readonly TimeSpan StaleRunningThreshold =
        TimeSpan.FromMinutes(VideoDeliveryFramework.MaxPollMinutes + 5);
    /// <summary>
    /// Wolverine 的处理器约定：公开方法、名字为 <c>Handle</c>、第一个参数是消息类型，
    /// 其余参数（这里是 CancellationToken）由框架注入。
    /// </summary>
    public async Task Handle(VideoGenerationRequested message, CancellationToken cancellationToken)
    {
        // 消费 span。真正耗时的模型调用全在这个 span 之下，
        // 面板上从「提交任务」到「出视频」是一条完整的树。
        using var activity = TraceSources.WorkerSource.StartMessageConsume(
            operation: "video.generate",
            messageType: nameof(VideoGenerationRequested),
            parentContext: TraceSources.ExtractContext(message.TraceHeaders),
            messageId: message.MessageId);

        activity?.SetTag("wv.task_id", message.TaskId.ToString());
        activity?.SetTag("wv.model_config_id", message.ModelConfigId.ToString());
        activity.TagCurrentUser(message.OwnerId);
        activity?.SetTag("wv.request.has_first_frame", message.FirstFrame is not null);
        activity?.SetTag("wv.request.duration_seconds", message.DurationSeconds);
        activity?.SetTag("wv.request.resolution", message.Resolution);

        var task = await taskRepository.GetByIdAsync(message.TaskId, cancellationToken);
        if (task is null)
        {
            logger.LogWarning("队列消息指向的任务 {TaskId} 不存在，消息直接丢弃", message.TaskId);
            activity?.SetTag("wv.skipped_reason", "task_not_found");
            return;
        }

        // 幂等保护 1：已经成功或已被用户取消，不再重复执行
        if (task.Status is GenerationTaskStatus.Succeeded or GenerationTaskStatus.Canceled)
        {
            logger.LogInformation("任务 {TaskId} 当前状态为 {Status}，跳过本次生成", task.Id, task.Status);
            activity?.SetTag("wv.skipped_reason", $"already_{task.Status}");
            return;
        }

        // 幂等保护 2：同一任务已被别的 Worker 实例置为运行中且尚未结束，交给它继续处理。
        //
        // 但"还在 Running"不等于"还活着"——上一次执行可能已经崩了、进程被杀了、
        // 或者写库失败导致状态没回写。这时若照旧跳过，任务就永远停在 Running。
        // 因此加一个心跳判断：超过阈值没有任何动静，本次就接管过来重新跑。
        if (task.Status == GenerationTaskStatus.Running && task.StartedAt is not null)
        {
            var silentFor = DateTimeOffset.UtcNow - task.UpdatedAt;

            if (silentFor < StaleRunningThreshold)
            {
                logger.LogInformation("任务 {TaskId} 已在生成中，跳过本次重复消息", task.Id);
                activity?.SetTag("wv.skipped_reason", "already_running");
                return;
            }

            logger.LogWarning(
                "任务 {TaskId} 处于 Running 但已 {Minutes:F0} 分钟没有心跳，判定上一次执行已中断，本次接管继续",
                task.Id,
                silentFor.TotalMinutes);

            activity?.SetTag("wv.stale_running_minutes", silentFor.TotalMinutes);
        }

        var model = await modelRepository.GetByIdAsync(message.ModelConfigId, cancellationToken);
        if (model is null)
        {
            await MarkFailedAsync(task, "模型配置已不存在或已被删除，无法执行生成。", cancellationToken);
            return;
        }

        await MarkRunningAsync(task, "任务已开始执行", cancellationToken);

        // 进度泵：把「已提交 / 排队中 / 生成中 / 下载保存」等阶段实时推给前端。
        // 视频动辄几分钟，没有进度推进用户会直接以为页面卡死。
        var pump = new TaskProgressPump(taskRepository, notifier, task, logger);
        var pumpTask = pump.RunAsync(cancellationToken);

        try
        {
            // 参数快照是 Worker 的唯一依据：从快照里取模型扩展参数，
            // 这样任务提交后即便模型配置被改动，重放出来的请求也保持一致。
            var parameters = GenerationTaskSerialization.DeserializeParams(message.ParamsJson);

            var request = new VideoGenerationRequest(
                task.Id,
                task.OwnerId,
                task.Prompt,
                task.NegativePrompt,
                message.FirstFrame,
                message.Resolution,
                message.DurationSeconds,
                message.Fps,
                message.Seed,
                ExtractExtraParams(parameters),
                model,
                pump.Progress,
                message.Mode,
                message.References);

            await notifier.NotifyAsync(
                task,
                message: DescribeStage(request),
                cancellationToken: cancellationToken);

            var outputs = await videoGenerationService.GenerateAsync(request, cancellationToken);

            // 先收泵再写终态：否则泵里残留的进度可能把已经置为 100 的状态覆盖回去
            pump.Complete();
            await pumpTask;

            await MarkSucceededAsync(task, outputs, cancellationToken);

            activity?.SetTag("wv.output_count", outputs.Count);
            activity.RecordSuccess();
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            pump.Complete();
            await pumpTask;

            if (hostLifetime.ApplicationStopping.IsCancellationRequested)
            {
                // 只有宿主真的在停止时才保留 Running，等待重启后的重投/清扫。
                logger.LogWarning("任务 {TaskId} 因服务停止而中断，状态保持为 {Status}", task.Id, task.Status);
                activity?.SetTag("wv.skipped_reason", "service_shutdown");
                activity.RecordFailure(new OperationCanceledException("服务停止，生成中断", exception));
                throw;
            }

            // Wolverine 自己的执行超时也会取消同一个 token。旧逻辑把它误判成服务停止，
            // 导致任务停在 Running、消息进入死信队列，用户只能等僵尸清扫器。
            const string reason = "视频生成被消息处理器超时中断，请重试。";
            logger.LogError(exception, "任务 {TaskId} 被消息执行超时中断，立即收敛为失败", task.Id);
            activity?.SetTag("wv.skipped_reason", "message_execution_timeout");
            activity.RecordFailure(exception);

            try
            {
                // 原 cancellationToken 已取消，回写终态必须使用独立令牌。
                await MarkFailedAsync(task, reason, CancellationToken.None);
            }
            catch (Exception persistException)
            {
                logger.LogCritical(
                    persistException,
                    "任务 {TaskId} 超时中断且无法回写失败状态，将由僵尸任务清扫器兜底",
                    task.Id);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "任务 {TaskId} 视频生成失败", task.Id);
            activity.RecordFailure(exception);

            // 失败时直接丢弃泵里的残留进度，避免它继续写状态与终态打架
            pump.Complete();

            try
            {
                await pumpTask;
            }
            catch (Exception pumpError)
            {
                logger.LogDebug(pumpError, "任务 {TaskId} 的进度泵在收尾时抛出异常，已忽略", task.Id);
            }

            try
            {
                await MarkFailedAsync(task, exception.Message, cancellationToken);
            }
            catch (Exception persistException)
            {
                // 连失败状态都写不回去时，任务会一直停在 Running。
                // 生成失败的原因上面已经记过了，这里把"写不回去"单独喊出来，
                // 否则两个错误互相掩盖，事后只能看到一句无关的异常。
                logger.LogCritical(
                    persistException,
                    "任务 {TaskId} 生成失败且无法回写失败状态，将长期停留在 Running，由僵尸任务清扫器兜底",
                    task.Id);
            }
        }
    }

    /// <summary>
    /// 取出模型扩展参数。
    ///
    /// 必须剔除视频自身的固定字段（分辨率 / 时长 / 帧率 / 种子 / 音效开关），
    /// 因为它们已由消息单独承载并被协议层单独写入请求体；
    /// 不剔除会让同一个值以两个字段名各发一遍，多数网关会因此报「未知参数」。
    /// </summary>
    /// <summary>
    /// 进度文案带上模式与素材数量，用户能一眼确认系统理解的是哪种生成方式。
    /// 文案含糊时"生成出来的不是我要的"很难被当成配置问题来查。
    /// </summary>
    private static string DescribeStage(VideoGenerationRequest request)
    {
        var references = request.EffectiveReferences;
        if (references.Count == 0)
            return $"正在提交到模型（{request.Mode.ToDisplayName()} · 文生视频）";

        var images = references.Count(x => x.Kind == VideoReferenceKind.Image);
        var videos = references.Count(x => x.Kind == VideoReferenceKind.Video);
        var audios = references.Count(x => x.Kind == VideoReferenceKind.Audio);

        var parts = new List<string>();
        if (images > 0) parts.Add($"{images} 图");
        if (videos > 0) parts.Add($"{videos} 视频");
        if (audios > 0) parts.Add($"{audios} 音频");

        return $"正在提交到模型（{request.Mode.ToDisplayName()} · {string.Join("、", parts)}）";
    }

    private static IReadOnlyDictionary<string, object?> ExtractExtraParams(
        IReadOnlyDictionary<string, object?> parameters)
    {
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "resolution",
            "durationSeconds",
            "fps",
            "withAudio",
            "seed",
            "firstFrameUrl",
            "lastFrameUrl",
        };

        var extra = new Dictionary<string, object?>();

        foreach (var (key, value) in parameters)
        {
            if (string.IsNullOrWhiteSpace(key) || reserved.Contains(key))
                continue;

            extra[key] = value;
        }

        return extra;
    }

    private async Task MarkRunningAsync(
        GenerationTask task,
        string message,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        task.Status = GenerationTaskStatus.Running;
        task.Progress = 5;
        task.StartedAt ??= now;
        task.UpdatedAt = now;
        task.ErrorMessage = null;

        taskRepository.Update(task);
        await taskRepository.SaveChangesAsync(cancellationToken);

        await notifier.NotifyAsync(task, message: message, cancellationToken: cancellationToken);
    }

    private async Task MarkSucceededAsync(
        GenerationTask task,
        IReadOnlyList<TaskAsset> outputs,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        // 产物与状态在同一张表、同一次 SaveChanges 里写回，
        // 保证前端收到"已完成"通知时，产物一定已经可读
        task.OutputsJson = GenerationTaskSerialization.SerializeAssets(outputs);
        task.Status = GenerationTaskStatus.Succeeded;
        task.Progress = 100;
        task.FinishedAt = now;
        task.UpdatedAt = now;
        task.ErrorMessage = null;

        taskRepository.Update(task);
        await taskRepository.SaveChangesAsync(cancellationToken);

        logger.LogInformation("任务 {TaskId} 视频生成完成，产出 {Count} 个文件", task.Id, outputs.Count);

        await notifier.NotifyAsync(task, outputs?.ToList(), cancellationToken: cancellationToken);
    }

    private async Task MarkFailedAsync(
        GenerationTask task,
        string reason,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var errorMessage = reason.Length > 2000 ? reason[..2000] : reason;

        task.Status = GenerationTaskStatus.Failed;
        task.Progress = 100;
        task.ErrorMessage = errorMessage;
        task.FinishedAt = now;
        task.UpdatedAt = now;

        taskRepository.Update(task);
        await taskRepository.SaveChangesAsync(cancellationToken);

        logger.LogWarning("任务 {TaskId} 已置为失败：{Reason}", task.Id, errorMessage);

        await notifier.NotifyAsync(task, message: errorMessage, cancellationToken: cancellationToken);
    }
}