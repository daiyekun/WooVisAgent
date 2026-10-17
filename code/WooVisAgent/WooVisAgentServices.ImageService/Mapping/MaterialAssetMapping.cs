using System.Globalization;
using WooVisAgent.SharedLib.Domain;
using WooVisAgentServices.ImageService.Dtos;

namespace WooVisAgentServices.ImageService.Mapping;

/// <summary>作品实体与出参之间的映射</summary>
public static class MaterialAssetMapping
{
    public static MaterialAssetDto ToDto(this MaterialAsset entity) => new(
        entity.Id.ToString(CultureInfo.InvariantCulture),
        entity.Type,
        entity.Url,
        entity.ThumbnailUrl,
        entity.FileName,
        entity.FileSize,
        entity.Width,
        entity.Height,
        entity.Duration,
        entity.GroupName,
        entity.Prompt,
        entity.SourceTaskId?.ToString(CultureInfo.InvariantCulture),
        entity.SourceTaskName,
        entity.CreatedAt);

    /// <summary>
    /// 把任务产物转成一条待入库的作品。
    /// 任务名与提示词一并快照进来，任务日后被删除也不影响作品列表展示。
    /// </summary>
    public static MaterialAsset ToMaterial(
        this TaskAsset asset,
        long id,
        string ownerId,
        long sourceTaskId,
        string? sourceTaskName,
        string? prompt,
        string? groupName,
        DateTimeOffset createdAt) => new()
    {
        Id = id,
        OwnerId = ownerId,
        SourceTaskId = sourceTaskId,
        SourceTaskName = sourceTaskName,
        SourceAssetId = asset.Id,
        Type = asset.Type,
        Url = asset.Url,
        ThumbnailUrl = asset.ThumbnailUrl,
        FileName = asset.FileName,
        FileSize = asset.FileSize,
        Width = asset.Width,
        Height = asset.Height,
        Duration = asset.Duration,
        GroupName = groupName,
        Prompt = prompt,
        CreatedAt = createdAt,
    };
}