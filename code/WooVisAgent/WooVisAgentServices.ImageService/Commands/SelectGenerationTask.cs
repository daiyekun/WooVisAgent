using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WooVisAgent.Services.Common.Contracts;
using WooVisAgent.SharedLib.Domain;
using WooVisAgent.SharedLib.Ids;
using WooVisAgent.SharedLib.Messaging;
using WooVisAgent.SharedLib.Repository;
using WooVisAgent.SharedLib.Result;
using WooVisAgentServices.ImageService.Dtos;
using WooVisAgentServices.ImageService.Mapping;
using WooVisAgentServices.ImageService.Serialization;

namespace WooVisAgentServices.ImageService.Commands;

/// <summary>
/// 选秀：把一条已完成任务的全部产物收录进「我的作品」。
///
/// 前端交互：任务列表每张卡片上的「选秀」按钮，位置在「重试」旁边。
///
/// 三条硬约束（任一不满足直接拒绝，避免作品库里出现半截数据）：
/// <list type="number">
///   <item>任务必须已成功完成——只有真正产出过东西的任务才谈得上入选</item>
///   <item>同一任务只允许入选一次——重复点击不产生第二条记录</item>
///   <item>任务必须还有产物——产物为空说明 Worker 没写回结果，入库无意义</item>
/// </list>
///
/// 成功后把 <see cref="GenerationTask.IsSelected" /> 置位，前端据此禁用按钮并显示星标角标。
/// </summary>
public record SelectGenerationTaskCommand(long Id, string? GroupName = null)
    : ICommand<Result<SelectGenerationTaskResultDto>>;

public class SelectGenerationTaskCommandHandler(
    IRepository<GenerationTask> taskRepository,
    IReadRepository<MaterialAsset> materialRepository,
    IRepository<MaterialAsset> materialWriteRepository,
    ICurrentUser currentUser,
    ISnowflakeIdGenerator idGenerator,
    ILogger<SelectGenerationTaskCommandHandler> logger)
    : ICommandHandler<SelectGenerationTaskCommand, Result<SelectGenerationTaskResultDto>>
{
    /// <summary>单个任务最多入库的作品数，防止异常的超大 batchSize 拖垮请求</summary>
    private const int MaxAssetsPerTask = 32;

    public async Task<Result<SelectGenerationTaskResultDto>> Handle(
        SelectGenerationTaskCommand command,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || string.IsNullOrEmpty(currentUser.Id))
            return Result.Unauthorized("未登录或登录状态已失效。");

        var ownerId = currentUser.Id;
        var task = await taskRepository.GetAsync(
            x => x.Id == command.Id && x.OwnerId == ownerId,
            cancellationToken: cancellationToken);

        if (task is null)
            return Result.NotFound("任务不存在或无权访问。");

        // 只有成功完成的任务才允许入选
        if (task.Status != GenerationTaskStatus.Succeeded)
            return Result.Invalid("任务尚未成功完成，无法入选我的作品。");

        // 幂等：已入选直接拦下，不重复入库
        if (task.IsSelected)
            return Result.Invalid("该任务已入选我的作品，无需重复操作。");

        var outputs = GenerationTaskSerialization.DeserializeAssets(task.OutputsJson);
        if (outputs.Count == 0)
            return Result.Invalid("该任务没有生成产物，无法入选我的作品。");

        // 双保险：即便 IsSelected 因历史脏数据为 false，只要作品库里已有该任务的记录也拒绝
        var alreadyArchived = await materialRepository.GetQueryable()
            .AsNoTracking()
            .AnyAsync(x => x.OwnerId == ownerId && x.SourceTaskId == task.Id, cancellationToken);

        if (alreadyArchived)
        {
            // 顺手把标记补上，避免下次又走到这里
            task.IsSelected = true;
            task.UpdatedAt = DateTimeOffset.UtcNow;
            taskRepository.Update(task);
            await taskRepository.SaveChangesAsync(cancellationToken);

            return Result.Invalid("该任务已入选我的作品，无需重复操作。");
        }

        var now = DateTimeOffset.UtcNow;
        var groupName = NormalizeGroupName(command.GroupName);
        var assets = outputs.Take(MaxAssetsPerTask).ToList();

        foreach (var asset in assets)
        {
            var material = asset.ToMaterial(
                idGenerator.NextId(),
                ownerId,
                task.Id,
                task.Name,
                task.Prompt,
                groupName,
                now);

            materialWriteRepository.Add(material);
        }

        task.IsSelected = true;
        task.UpdatedAt = now;
        taskRepository.Update(task);

        await taskRepository.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "任务 {TaskId} 已选秀入库，共 {Count} 件作品",
            task.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            assets.Count);

        return Result.Success(new SelectGenerationTaskResultDto(assets.Count, task.ToDto()));
    }

    /// <summary>分组名去空白并限长，超长直接丢弃而不是让数据库报错</summary>
    private static string? NormalizeGroupName(string? groupName)
    {
        if (string.IsNullOrWhiteSpace(groupName))
            return null;

        var trimmed = groupName.Trim();
        return trimmed.Length > 64 ? trimmed[..64] : trimmed;
    }
}