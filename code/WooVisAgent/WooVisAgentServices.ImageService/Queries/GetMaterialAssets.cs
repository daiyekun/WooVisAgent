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
/// 分页查询当前用户的作品（我的产品），可按类型 / 分组 / 关键词筛选。
///
/// 关键词同时匹配文件名与来源任务名，用户既可以按生成结果找，
/// 也可以按当初的任务名反查。对应前端 <c>MaterialLibraryView.vue</c>。
/// </summary>
public record GetMaterialAssetsQuery(
    string? Type = null,
    string? GroupName = null,
    string? Keyword = null,
    int Page = 1,
    int PageSize = 24) : IQuery<Result<PagedResultDto<MaterialAssetDto>>>;

public class GetMaterialAssetsQueryHandler(
    IRepository<MaterialAsset> repository,
    ICurrentUser currentUser)
    : IQueryHandler<GetMaterialAssetsQuery, Result<PagedResultDto<MaterialAssetDto>>>
{
    private const int MaxPageSize = 200;

    public async Task<Result<PagedResultDto<MaterialAssetDto>>> Handle(
        GetMaterialAssetsQuery query,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || string.IsNullOrEmpty(currentUser.Id))
            return Result.Unauthorized("未登录或登录状态已失效。");

        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize < 1 ? 24 : Math.Min(query.PageSize, MaxPageSize);

        var sql = repository.GetQueryable()
            .Where(x => x.OwnerId == currentUser.Id);

        // 类型只认 image / video，其它值直接忽略而不是返回空列表——
        // 前端类型筛选器传错时用户更希望看到全部，而不是一个空页面
        var type = query.Type?.Trim().ToLowerInvariant();
        if (type is "image" or "video")
        {
            sql = sql.Where(x => x.Type == type);
        }

        if (!string.IsNullOrWhiteSpace(query.GroupName))
        {
            var groupName = query.GroupName.Trim();
            sql = sql.Where(x => x.GroupName == groupName);
        }

        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = $"%{query.Keyword.Trim()}%";
            sql = sql.Where(x =>
                EF.Functions.Like(x.FileName ?? string.Empty, keyword)
                || EF.Functions.Like(x.SourceTaskName ?? string.Empty, keyword)
                || EF.Functions.Like(x.Prompt ?? string.Empty, keyword));
        }

        var total = await sql.CountAsync(cancellationToken);

        var items = await sql
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return Result.Success(new PagedResultDto<MaterialAssetDto>(
            items.Select(x => x.ToDto()).ToList(),
            total,
            page,
            pageSize));
    }
}

/// <summary>作品库的分组列表，供前端渲染筛选页签</summary>
public record GetMaterialGroupsQuery : IQuery<Result<IReadOnlyList<string>>>;

public class GetMaterialGroupsQueryHandler(
    IRepository<MaterialAsset> repository,
    ICurrentUser currentUser)
    : IQueryHandler<GetMaterialGroupsQuery, Result<IReadOnlyList<string>>>
{
    public async Task<Result<IReadOnlyList<string>>> Handle(
        GetMaterialGroupsQuery query,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || string.IsNullOrEmpty(currentUser.Id))
            return Result.Unauthorized("未登录或登录状态已失效。");

        var groups = await repository.GetQueryable()
            .Where(x => x.OwnerId == currentUser.Id && x.GroupName != null && x.GroupName != string.Empty)
            .Select(x => x.GroupName!)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(cancellationToken);

        return Result.Success<IReadOnlyList<string>>(groups);
    }
}