using Microsoft.EntityFrameworkCore;
using WooVisAgent.Services.Common.Contracts;
using WooVisAgent.SharedLib.Domain;
using WooVisAgent.SharedLib.Messaging;
using WooVisAgent.SharedLib.Repository;
using WooVisAgent.SharedLib.Result;
using WooVisAgentServices.ImageService.Dtos;
using WooVisAgentServices.ImageService.Mapping;

namespace WooVisAgentServices.ImageService.Queries;

/// <summary>
/// 分页查询当前用户的任务列表，可按类型 / 状态 / 关键词 / 模型筛选。
/// 类型与状态传非法值时直接返回空列表，避免误把全部数据带出去。
/// </summary>
public record GetGenerationTasksQuery(
    string? Type = null,
    string? Status = null,
    string? Keyword = null,
    string? ModelId = null,
    int Page = 1,
    int PageSize = 12) : IQuery<Result<PagedResultDto<GenerationTaskDto>>>;

public class GetGenerationTasksQueryHandler(
    IRepository<GenerationTask> repository,
    ICurrentUser currentUser)
    : IQueryHandler<GetGenerationTasksQuery, Result<PagedResultDto<GenerationTaskDto>>>
{
    private const int MaxPageSize = 200;

    public async Task<Result<PagedResultDto<GenerationTaskDto>>> Handle(
        GetGenerationTasksQuery query,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || string.IsNullOrEmpty(currentUser.Id))
            return Result.Unauthorized("未登录或登录状态已失效。");

        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize < 1 ? 12 : Math.Min(query.PageSize, MaxPageSize);

        var ownerId = currentUser.Id;
        var sql = repository.GetQueryable().Where(x => x.OwnerId == ownerId);

        if (!string.IsNullOrWhiteSpace(query.Type))
        {
            if (!GenerationTaskTypeExtensions.TryParseWireValue(query.Type, out var taskType))
                return Result.Success(new PagedResultDto<GenerationTaskDto>([], 0, page, pageSize));

            sql = sql.Where(x => x.Type == taskType);
        }

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            if (!GenerationTaskStatusExtensions.TryParseWireValue(query.Status, out var status))
                return Result.Success(new PagedResultDto<GenerationTaskDto>([], 0, page, pageSize));

            sql = sql.Where(x => x.Status == status);
        }

        if (long.TryParse(query.ModelId, out var modelConfigId))
            sql = sql.Where(x => x.ModelConfigId == modelConfigId);

        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = $"%{query.Keyword.Trim()}%";
            sql = sql.Where(x => EF.Functions.Like(x.Name, keyword) || EF.Functions.Like(x.Prompt, keyword));
        }

        var total = await sql.CountAsync(cancellationToken);

        var items = await sql
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var result = new PagedResultDto<GenerationTaskDto>(
            items.Select(x => x.ToDto()).ToList(),
            total,
            page,
            pageSize);

        return Result.Success(result);
    }
}
