using WooVisAgent.Services.Common.Contracts;
using WooVisAgent.SharedLib.Domain;
using WooVisAgent.SharedLib.Messaging;
using WooVisAgent.SharedLib.Repository;
using WooVisAgent.SharedLib.Result;
using WooVisAgentServices.ImageService.Dtos;
using WooVisAgentServices.ImageService.Mapping;

namespace WooVisAgentServices.ImageService.Queries;

/// <summary>
/// 按主键批量查询任务状态。
/// 前端在 SignalR 不可用时用它做兜底轮询，所以只返回状态相关的轻量字段。
/// </summary>
public record GetGenerationTaskStatusesQuery(IReadOnlyList<string> Ids)
    : IQuery<Result<IReadOnlyList<TaskStatusDto>>>;

public class GetGenerationTaskStatusesQueryHandler(
    IRepository<GenerationTask> repository,
    ICurrentUser currentUser)
    : IQueryHandler<GetGenerationTaskStatusesQuery, Result<IReadOnlyList<TaskStatusDto>>>
{
    private const int MaxIds = 200;

    public async Task<Result<IReadOnlyList<TaskStatusDto>>> Handle(
        GetGenerationTaskStatusesQuery query,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || string.IsNullOrEmpty(currentUser.Id))
            return Result.Unauthorized("未登录或登录状态已失效。");

        var ids = (query.Ids ?? [])
            .Select(id => long.TryParse(id, out var parsed) ? parsed : (long?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .Take(MaxIds)
            .ToList();

        if (ids.Count == 0)
            return Result.Success<IReadOnlyList<TaskStatusDto>>([]);

        var ownerId = currentUser.Id;
        var tasks = await repository.GetListAsync(
            x => x.OwnerId == ownerId && ids.Contains(x.Id),
            cancellationToken);

        return Result.Success<IReadOnlyList<TaskStatusDto>>(
            tasks.Select(x => x.ToStatusDto()).ToList());
    }
}
