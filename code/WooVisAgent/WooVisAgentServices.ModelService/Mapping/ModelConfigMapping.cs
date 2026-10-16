using System.Globalization;
using WooVisAgent.SharedLib.Domain;
using WooVisAgentServices.ModelService.Dtos;
using WooVisAgentServices.ModelService.Serialization;

namespace WooVisAgentServices.ModelService.Mapping;

/// <summary>实体与出参之间的映射</summary>
public static class ModelConfigMapping
{
    public static ModelConfigDto ToDto(this ModelConfig entity) => new(
        entity.Id.ToString(CultureInfo.InvariantCulture),
        entity.Name,
        entity.Provider,
        entity.Category.ToWireValue(),
        entity.ModelKey,
        entity.Endpoint,
        entity.Enabled,
        entity.IsDefault,
        ModelParamsSerializer.Deserialize(entity.ParamsJson),
        entity.Remark,
        entity.CreatedAt,
        entity.UpdatedAt);
}
