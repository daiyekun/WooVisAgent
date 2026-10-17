using System.Globalization;
using WooVisAgent.SharedLib.Domain;
using WooVisAgentServices.ImageService.Dtos;
using WooVisAgentServices.ImageService.Serialization;

namespace WooVisAgentServices.ImageService.Mapping;

/// <summary>实体与出参之间的映射</summary>
public static class GenerationTaskMapping
{
    public static TaskAssetDto ToDto(this TaskAsset asset) => new(
        asset.Id,
        asset.Type,
        asset.Url,
        asset.ThumbnailUrl,
        asset.FileName,
        asset.FileSize,
        asset.Width,
        asset.Height,
        asset.Duration);

    public static GenerationTaskDto ToDto(this GenerationTask entity) => new(
        entity.Id.ToString(CultureInfo.InvariantCulture),
        entity.Name,
        entity.Type.ToWireValue(),
        entity.Status.ToWireValue(),
        entity.Progress,
        entity.ModelConfigId.ToString(CultureInfo.InvariantCulture),
        entity.ModelName,
        entity.Prompt,
        entity.NegativePrompt,
        GenerationTaskSerialization.DeserializeParams(entity.ParamsJson),
        GenerationTaskSerialization.DeserializeAssets(entity.InputsJson).Select(x => x.ToDto()).ToList(),
        GenerationTaskSerialization.DeserializeAssets(entity.OutputsJson).Select(x => x.ToDto()).ToList(),
        entity.ErrorMessage,
        entity.CreatedAt,
        entity.StartedAt,
        entity.FinishedAt,
        entity.OwnerId,
        entity.IsSelected,
        entity.ConversationId?.ToString(CultureInfo.InvariantCulture),
        entity.SourceTaskId?.ToString(CultureInfo.InvariantCulture));

    public static TaskStatusDto ToStatusDto(this GenerationTask entity) => new(
        entity.Id.ToString(CultureInfo.InvariantCulture),
        entity.Status.ToWireValue(),
        entity.Progress,
        entity.ErrorMessage);
}
