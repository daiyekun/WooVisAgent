using WooVisAgent.Services.Common.Contracts;
using WooVisAgent.SharedLib.Domain;
using WooVisAgent.SharedLib.Messaging;
using WooVisAgent.SharedLib.Repository;
using WooVisAgent.SharedLib.Result;
using WooVisAgentServices.ImageService.Dtos;
using WooVisAgentServices.ImageService.Mapping;

namespace WooVisAgentServices.ImageService.Queries;

/// <summary>查询单条任务详情（含输入素材与产物）</summary>
public record GetGenerationTaskDetailQuery(long Id) : IQuery<Result<GenerationTaskDto>>;

public class GetGenerationTaskDetailQueryHandler(
    IRepository<GenerationTask> repository,
    ICurrentUser currentUser)
    : IQueryHandler<GetGenerationTaskDetailQuery, Result<GenerationTaskDto>>
{
    public async Task<Result<GenerationTaskDto>> Handle(
        GetGenerationTaskDetailQuery query,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || string.IsNullOrEmpty(currentUser.Id))
            return Result.Unauthorized("未登录或登录状态已失效。");

        var ownerId = currentUser.Id;
        var task = await repository.GetAsync(
            x => x.Id == query.Id && x.OwnerId == ownerId,
            cancellationToken: cancellationToken);

        if (task is null)
            return Result.NotFound("任务不存在或无权访问。");

        return Result.Success(task.ToDto());
    }
}

/// <summary>查询任务产物，前端「任务产物」接口使用</summary>
public record GetGenerationTaskOutputsQuery(long Id) : IQuery<Result<IReadOnlyList<TaskAssetDto>>>;

public class GetGenerationTaskOutputsQueryHandler(
    IRepository<GenerationTask> repository,
    ICurrentUser currentUser)
    : IQueryHandler<GetGenerationTaskOutputsQuery, Result<IReadOnlyList<TaskAssetDto>>>
{
    public async Task<Result<IReadOnlyList<TaskAssetDto>>> Handle(
        GetGenerationTaskOutputsQuery query,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || string.IsNullOrEmpty(currentUser.Id))
            return Result.Unauthorized("未登录或登录状态已失效。");

        var ownerId = currentUser.Id;
        var task = await repository.GetAsync(
            x => x.Id == query.Id && x.OwnerId == ownerId,
            cancellationToken: cancellationToken);

        if (task is null)
            return Result.NotFound("任务不存在或无权访问。");

        IReadOnlyList<TaskAssetDto> outputs = GenerationTaskMapping
            .ToDto(task)
            .Outputs;

        return Result.Success(outputs);
    }
}
