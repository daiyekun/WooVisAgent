using Microsoft.EntityFrameworkCore;
using WooVisAgent.Services.Common.Contracts;
using WooVisAgent.SharedLib.Domain;
using WooVisAgent.SharedLib.Messaging;
using WooVisAgent.SharedLib.Repository;
using WooVisAgent.SharedLib.Result;
using WooVisAgentServices.ModelService.Dtos;
using WooVisAgentServices.ModelService.Mapping;

namespace WooVisAgentServices.ModelService.Queries;

/// <summary>查询当前用户的模型配置列表，可按分类筛选</summary>
public record GetModelConfigsQuery(string? Category = null) : IQuery<Result<IReadOnlyList<ModelConfigDto>>>;

public class GetModelConfigsQueryHandler(
    IRepository<ModelConfig> repository,
    ICurrentUser currentUser) : IQueryHandler<GetModelConfigsQuery, Result<IReadOnlyList<ModelConfigDto>>>
{
    public async Task<Result<IReadOnlyList<ModelConfigDto>>> Handle(
        GetModelConfigsQuery query,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || string.IsNullOrEmpty(currentUser.Id))
            return Result.Unauthorized("未登录或登录状态已失效。");

        var ownerId = currentUser.Id;

        // 分类参数可选：传了非法值直接返回空列表，避免误把全部数据带出去
        ModelCategory? category = null;
        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            if (!ModelCategoryExtensions.TryParseWireValue(query.Category, out var parsed))
                return Result.Success<IReadOnlyList<ModelConfigDto>>([]);

            category = parsed;
        }

        var sql = repository.GetQueryable().Where(x => x.OwnerId == ownerId);
        if (category is { } selectedCategory)
            sql = sql.Where(x => x.Category == selectedCategory);

        var entities = await sql
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .ToListAsync(cancellationToken);

        IReadOnlyList<ModelConfigDto> result = entities.Select(x => x.ToDto()).ToList();
        return Result.Success(result);
    }
}
