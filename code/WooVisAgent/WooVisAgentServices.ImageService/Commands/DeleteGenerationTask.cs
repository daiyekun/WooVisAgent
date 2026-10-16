using WooVisAgent.Services.Common.Contracts;
using WooVisAgent.SharedLib.Domain;
using WooVisAgent.SharedLib.Messaging;
using WooVisAgent.SharedLib.Repository;
using WooVisAgent.SharedLib.Result;

namespace WooVisAgentServices.ImageService.Commands;

/// <summary>删除单个任务（同时移除其产物记录，磁盘文件不做物理删除）</summary>
public record DeleteGenerationTaskCommand(long Id) : ICommand<Result>;

public class DeleteGenerationTaskCommandHandler(
    IRepository<GenerationTask> repository,
    ICurrentUser currentUser) : ICommandHandler<DeleteGenerationTaskCommand, Result>
{
    public async Task<Result> Handle(DeleteGenerationTaskCommand command, CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || string.IsNullOrEmpty(currentUser.Id))
            return Result.Unauthorized("未登录或登录状态已失效。");

        var ownerId = currentUser.Id;
        var task = await repository.GetAsync(
            x => x.Id == command.Id && x.OwnerId == ownerId,
            cancellationToken: cancellationToken);

        if (task is null)
            return Result.NotFound("任务不存在或无权访问。");

        repository.Delete(task);
        await repository.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}

/// <summary>批量删除任务</summary>
public record BatchDeleteGenerationTasksCommand(IReadOnlyList<string> Ids) : ICommand<Result>;

public class BatchDeleteGenerationTasksCommandHandler(
    IRepository<GenerationTask> repository,
    ICurrentUser currentUser) : ICommandHandler<BatchDeleteGenerationTasksCommand, Result>
{
    private const int MaxBatchSize = 200;

    public async Task<Result> Handle(
        BatchDeleteGenerationTasksCommand command,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || string.IsNullOrEmpty(currentUser.Id))
            return Result.Unauthorized("未登录或登录状态已失效。");

        var ids = (command.Ids ?? [])
            .Select(id => long.TryParse(id, out var parsed) ? parsed : (long?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .Take(MaxBatchSize)
            .ToList();

        if (ids.Count == 0)
            return Result.Invalid("请选择要删除的任务。");

        var ownerId = currentUser.Id;
        var tasks = await repository.GetListAsync(
            x => x.OwnerId == ownerId && ids.Contains(x.Id),
            cancellationToken);

        foreach (var task in tasks)
            repository.Delete(task);

        await repository.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
