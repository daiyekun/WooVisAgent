using System.Globalization;
using Microsoft.Extensions.Logging;
using WooVisAgent.Services.Common.Contracts;
using WooVisAgent.SharedLib.Domain;
using WooVisAgent.SharedLib.Ids;
using WooVisAgent.SharedLib.Messaging;
using WooVisAgent.SharedLib.Repository;
using WooVisAgent.SharedLib.Result;
using WooVisAgentServices.ImageService.Contracts;
using WooVisAgentServices.ImageService.Dtos;
using WooVisAgentServices.ImageService.Mapping;

namespace WooVisAgentServices.ImageService.Commands;

/// <summary>
/// 复用原任务参数重新提交一条新任务。
///
/// 注意是"新建一条任务"而不是"重置原任务"：原任务的产物与状态作为历史保留，
/// 新任务有独立主键，消息重放时不会和旧任务互相干扰。
/// </summary>
public record RetryGenerationTaskCommand(long Id) : ICommand<Result<GenerationTaskDto>>;

public class RetryGenerationTaskCommandHandler(
    IRepository<GenerationTask> repository,
    ICurrentUser currentUser,
    ISnowflakeIdGenerator idGenerator,
    IGenerationTaskDispatcher dispatcher,
    ILogger<RetryGenerationTaskCommandHandler> logger)
    : ICommandHandler<RetryGenerationTaskCommand, Result<GenerationTaskDto>>
{
    public async Task<Result<GenerationTaskDto>> Handle(
        RetryGenerationTaskCommand command,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || string.IsNullOrEmpty(currentUser.Id))
            return Result.Unauthorized("未登录或登录状态已失效。");

        var ownerId = currentUser.Id;
        var source = await repository.GetAsync(
            x => x.Id == command.Id && x.OwnerId == ownerId,
            cancellationToken: cancellationToken);

        if (source is null)
            return Result.NotFound("任务不存在或无权访问。");

        // 已入选「我的作品」的任务不再允许重试：
        // 重试会生成一份新产物，而作品库里已经躺着旧产物的引用，
        // 两份产物同名同源会让用户分不清哪张才是自己选中的那件。
        if (source.IsSelected)
            return Result.Invalid("该任务已入选我的作品，请先在作品库中移除后再重试。");

        if (!source.Status.IsFinal())
            return Result.Invalid("任务尚未结束，请先取消或等待完成。");

        var now = DateTimeOffset.UtcNow;

        var task = new GenerationTask
        {
            Id = idGenerator.NextId(),
            Name = source.Name,
            OwnerId = source.OwnerId,
            Type = source.Type,
            Status = GenerationTaskStatus.Queued,
            Progress = 0,
            ModelConfigId = source.ModelConfigId,
            ModelName = source.ModelName,
            Provider = source.Provider,
            Prompt = source.Prompt,
            NegativePrompt = source.NegativePrompt,
            ParamsJson = source.ParamsJson,
            InputsJson = source.InputsJson,
            OutputsJson = "[]",
            RetryCount = source.RetryCount + 1,
            CreatedAt = now,
            QueuedAt = now,
            UpdatedAt = now,
        };

        repository.Add(task);
        await repository.SaveChangesAsync(cancellationToken);

        try
        {
            await dispatcher.DispatchAsync(task, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "重试任务 {TaskId} 投递到消息队列失败", task.Id);

            task.Status = GenerationTaskStatus.Failed;
            task.ErrorMessage = $"任务投递到消息队列失败：{exception.Message}";
            task.FinishedAt = DateTimeOffset.UtcNow;
            task.UpdatedAt = DateTimeOffset.UtcNow;
            repository.Update(task);
            await repository.SaveChangesAsync(cancellationToken);

            return Result.Failure("任务已创建，但投递到消息队列失败，请稍后重试。");
        }

        logger.LogInformation("任务 {SourceId} 已按原参数重试，新任务 {TaskId}",
            source.Id, task.Id.ToString(CultureInfo.InvariantCulture));

        return Result.Success(task.ToDto());
    }
}
