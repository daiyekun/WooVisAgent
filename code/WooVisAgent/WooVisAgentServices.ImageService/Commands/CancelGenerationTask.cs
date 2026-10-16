using WooVisAgent.Services.Common.Contracts;
using WooVisAgent.SharedLib.Domain;
using WooVisAgent.SharedLib.Messaging;
using WooVisAgent.SharedLib.Repository;
using WooVisAgent.SharedLib.Result;
using WooVisAgentServices.ImageService.Dtos;
using WooVisAgentServices.ImageService.Mapping;

namespace WooVisAgentServices.ImageService.Commands;

/// <summary>取消进行中的生成任务</summary>
public record CancelGenerationTaskCommand(long Id) : ICommand<Result<GenerationTaskDto>>;

public class CancelGenerationTaskCommandHandler(
    IRepository<GenerationTask> repository,
    ICurrentUser currentUser) : ICommandHandler<CancelGenerationTaskCommand, Result<GenerationTaskDto>>
{
    public async Task<Result<GenerationTaskDto>> Handle(
        CancelGenerationTaskCommand command,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || string.IsNullOrEmpty(currentUser.Id))
            return Result.Unauthorized("未登录或登录状态已失效。");

        var ownerId = currentUser.Id;
        var task = await repository.GetAsync(
            x => x.Id == command.Id && x.OwnerId == ownerId,
            cancellationToken: cancellationToken);

        if (task is null)
            return Result.NotFound("任务不存在或无权访问。");

        if (task.Status.IsFinal())
            return Result.Invalid("任务已结束，无需取消。");

        var now = DateTimeOffset.UtcNow;
        task.Status = GenerationTaskStatus.Canceled;
        task.FinishedAt = now;
        task.UpdatedAt = now;
        task.ErrorMessage = null;

        repository.Update(task);
        await repository.SaveChangesAsync(cancellationToken);

        // Worker 侧在开始执行前会再读一次状态，看到 Canceled 就直接丢弃，不会白跑一次生成
        return Result.Success(task.ToDto());
    }
}
