using Microsoft.Extensions.Logging;
using WooVisAgent.EventBus.Events;
using WooVisAgent.SharedLib.Diagnostics;
using WooVisAgent.SharedLib.Domain;
using WooVisAgent.SharedLib.Repository;
using WooVisAgent.GenerateWorker.Support;
using WooVisAgentServices.ImageService.Contracts;
using WooVisAgentServices.ImageService.Serialization;

namespace WooVisAgent.GenerateWorker.Consumers;

/// <summary>
/// 生成任务消费者：从队列 <c>WooVisAgent.GenerateWorker</c> 取任务，调用图片生成服务生成，再把状态写回数据库。
///
/// 状态流转：Queued →（置 Running）→ 生成 → Succeeded / Failed。
///
/// 两个关键设计：
/// <list type="number">
///   <item>
///     <b>幂等</b>：任务已是成功或已取消时直接跳过，因此消息重复投递不会重复生成、重复扣费。
///   </item>
///   <item>
///     <b>异常不外抛</b>：生成失败在本方法内被捕获并落库为 Failed（同时通知前端），
///     不会再抛回消息总线。否则消息会被反复重投，形成"生成 → 失败 → 重投 → 再生成"的死循环。
///   </item>
/// </list>
/// </summary>
public class ImageGenerationRequestedConsumer(
    IRepository<GenerationTask> taskRepository,
    IRepository<ModelConfig> modelRepository,
    IImageGenerationService imageGenerationService,
    IGenerationTaskNotifier notifier,
    ILogger<ImageGenerationRequestedConsumer> logger)
{
    /// <summary>
    /// Wolverine 的处理器约定：公开方法、名字为 <c>Handle</c>、第一个参数是消息类型，
    /// 其余参数（这里是 CancellationToken）由框架注入。
    /// </summary>
    public async Task Handle(ImageGenerationRequested message, CancellationToken cancellationToken)
    {
        // 消费 span。真正耗时的模型调用全在这个 span 之下，
        // 面板上从「提交任务」到「出图」是一条完整的树，而不是两段互不相干的记录。
        using var activity = TraceSources.WorkerSource.StartMessageConsume(
            operation: "image.generate",
            messageType: nameof(ImageGenerationRequested),
            parentContext: TraceSources.ExtractContext(message.TraceHeaders),
            messageId: message.MessageId);

        activity?.SetTag("wv.task_id", message.TaskId.ToString());
        activity?.SetTag("wv.model_config_id", message.ModelConfigId.ToString());
        activity.TagCurrentUser(message.OwnerId);
        activity?.SetTag("wv.request.width", message.Width);
        activity?.SetTag("wv.request.height", message.Height);
        activity?.SetTag("wv.request.batch_size", message.BatchSize);
        activity?.SetTag("wv.request.reference_images", message.ReferenceImages?.Count ?? 0);

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

        // 幂等保护 2：同一任务已被别的 Worker 实例置为运行中且尚未结束，交给它继续处理
        if (task.Status == GenerationTaskStatus.Running && task.StartedAt is not null)
        {
            logger.LogInformation("任务 {TaskId} 已在生成中，跳过本次重复消息", task.Id);
            activity?.SetTag("wv.skipped_reason", "already_running");
            return;
        }

        var model = await modelRepository.GetByIdAsync(message.ModelConfigId, cancellationToken);
        if (model is null)
        {
            await MarkFailedAsync(task, "模型配置已不存在或已被删除，无法执行生成。", cancellationToken);
            return;
        }

        await MarkRunningAsync(task, "任务已开始执行", cancellationToken);

        // 进度泵：把生成过程中的阶段推进写回任务表并推给前端。
        // 以前整条任务只有「5%」和「100%」两个点，模型一慢进度条就像卡死；
        // 现在解析接口、提交、等待模型、落盘都会被真实反映出来。
        var pump = new TaskProgressPump(taskRepository, notifier, task);
        var pumpTask = pump.RunAsync(cancellationToken);

        try
        {
            var request = new ImageGenerationRequest(
                task.Id,
                task.OwnerId,
                task.Prompt,
                task.NegativePrompt,
                message.Width,
                message.Height,
                message.BatchSize,
                message.Steps,
                message.CfgScale,
                message.Seed,
                message.ReferenceImages,
                model,
                pump.Progress);

            await notifier.NotifyAsync(task, message: "正在调用模型生成", cancellationToken: cancellationToken);

            var outputs = await imageGenerationService.GenerateAsync(request, cancellationToken);

            // 先收泵再写终态：否则泵里残留的进度可能把已经置为 100 的状态覆盖回去
            pump.Complete();
            await pumpTask;

            await MarkSucceededAsync(task, outputs, cancellationToken);

            activity?.SetTag("wv.output_count", outputs.Count);
            activity.RecordSuccess();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 服务停止（而非业务失败）时保留 Running 状态，等下次重启由重投或手动重试继续
            logger.LogWarning("任务 {TaskId} 因服务停止而中断，状态保持为 {Status}", task.Id, task.Status);
            activity?.SetTag("wv.skipped_reason", "service_shutdown");
            activity.RecordFailure(new OperationCanceledException("服务停止，生成中断"));
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "任务 {TaskId} 生成失败", task.Id);
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

            await MarkFailedAsync(task, exception.Message, cancellationToken);
        }
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

        logger.LogInformation("任务 {TaskId} 生成完成，产出 {Count} 张图片", task.Id, outputs.Count);

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
