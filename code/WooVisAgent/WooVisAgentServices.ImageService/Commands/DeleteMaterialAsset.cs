using Microsoft.EntityFrameworkCore;
using WooVisAgent.Services.Common.Contracts;
using WooVisAgent.SharedLib.Domain;
using WooVisAgent.SharedLib.Messaging;
using WooVisAgent.SharedLib.Repository;
using WooVisAgent.SharedLib.Result;

namespace WooVisAgentServices.ImageService.Commands;

/// <summary>
/// 删除作品（我的产品）。
///
/// 删除只移除数据库记录，不动磁盘文件——同一个产物文件可能被任务产物列表引用着，
/// 直接删文件会让任务详情页出现坏图。
///
/// 删除后会把来源任务的 IsSelected 标记回落：该任务的产物已经不在作品库里了，
/// 若还留着角标，用户会看到一个"已入选但点不进去"的空壳状态。
/// </summary>
public record DeleteMaterialAssetCommand(string Id) : ICommand<Result>;

public class DeleteMaterialAssetCommandHandler(
    IRepository<MaterialAsset> materialRepository,
    IRepository<GenerationTask> taskRepository,
    ICurrentUser currentUser)
    : ICommandHandler<DeleteMaterialAssetCommand, Result>
{
    public async Task<Result> Handle(DeleteMaterialAssetCommand command, CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || string.IsNullOrEmpty(currentUser.Id))
            return Result.Unauthorized("未登录或登录状态已失效。");

        if (!long.TryParse(command.Id, out var materialId))
            return Result.Invalid("作品 ID 无效。");

        var ownerId = currentUser.Id;
        var material = await materialRepository.GetAsync(
            x => x.Id == materialId && x.OwnerId == ownerId,
            cancellationToken: cancellationToken);

        if (material is null)
            return Result.NotFound("作品不存在或无权访问。");

        var sourceTaskId = material.SourceTaskId;
        materialRepository.Delete(material);

        // 只有当该任务在作品库里已经一件不剩时，才把标记回落，
        // 否则删掉 batchSize=4 中的 1 张就整个任务都变回"未入选"是不合理的
        if (sourceTaskId is not null)
        {
            // 注意：Delete() 只是给实体打删除标记，同一 DbContext 里它仍处于跟踪状态，
            // 直接用 GetCountAsync 查询会把这条待删除的行也算进去，count 永远不为 0。
            // 因此这里用 AsNoTracking + 排除当前实体来数真实剩余数量。
            var removedId = material.Id;
            var remaining = await materialRepository.GetQueryable()
                .AsNoTracking()
                .CountAsync(
                    x => x.OwnerId == ownerId && x.SourceTaskId == sourceTaskId && x.Id != removedId,
                    cancellationToken);

            if (remaining == 0)
            {
                var task = await taskRepository.GetAsync(
                    x => x.Id == sourceTaskId.Value && x.OwnerId == ownerId,
                    cancellationToken: cancellationToken);

                if (task is not null && task.IsSelected)
                {
                    task.IsSelected = false;
                    task.UpdatedAt = DateTimeOffset.UtcNow;
                    taskRepository.Update(task);
                }
            }
        }

        await taskRepository.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

/// <summary>批量删除作品</summary>
public record BatchDeleteMaterialAssetsCommand(IReadOnlyList<string> Ids) : ICommand<Result>;

public class BatchDeleteMaterialAssetsCommandHandler(
    IRepository<MaterialAsset> materialRepository,
    IRepository<GenerationTask> taskRepository,
    ICurrentUser currentUser)
    : ICommandHandler<BatchDeleteMaterialAssetsCommand, Result>
{
    private const int MaxBatchSize = 200;

    public async Task<Result> Handle(
        BatchDeleteMaterialAssetsCommand command,
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
            return Result.Invalid("请选择要删除的作品。");

        var ownerId = currentUser.Id;
        var materials = await materialRepository.GetListAsync(
            x => x.OwnerId == ownerId && ids.Contains(x.Id),
            cancellationToken);

        var affectedTaskIds = materials
            .Where(x => x.SourceTaskId.HasValue)
            .Select(x => x.SourceTaskId!.Value)
            .Distinct()
            .ToList();

        foreach (var material in materials)
            materialRepository.Delete(material);

        // 与单条删除同理：作品被清空的任务要回落 IsSelected 标记。
        // 同样用 AsNoTracking，因为前面已把待删除实体挂进跟踪集，不排除会把它们算进来
        if (affectedTaskIds.Count > 0)
        {
            var deletedIds = materials.Select(x => x.Id).ToList();
            var stillArchived = await materialRepository.GetQueryable()
                .AsNoTracking()
                .Where(x => x.OwnerId == ownerId
                            && affectedTaskIds.Contains(x.SourceTaskId!.Value)
                            && !deletedIds.Contains(x.Id))
                .Select(x => x.SourceTaskId!.Value)
                .Distinct()
                .ToListAsync(cancellationToken);

            var clearedTaskIds = affectedTaskIds.Except(stillArchived).ToList();
            if (clearedTaskIds.Count > 0)
            {
                var tasks = await taskRepository.GetListAsync(
                    x => x.OwnerId == ownerId && clearedTaskIds.Contains(x.Id) && x.IsSelected,
                    cancellationToken);

                foreach (var task in tasks)
                {
                    task.IsSelected = false;
                    task.UpdatedAt = DateTimeOffset.UtcNow;
                    taskRepository.Update(task);
                }
            }
        }

        await taskRepository.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}