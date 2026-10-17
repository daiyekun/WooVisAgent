using System.Globalization;
using WooVisAgent.SharedLib.Delivery;
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
        entity.UpdatedAt,
        // 交付模板原样回传，让编辑页能显示用户当前配的是哪一套形状。
        // 解析失败时返回 null（= 用预设推断），而不是把坏 JSON 抛给前端
        VideoDeliveryResolver.ReadTemplate(entity.DeliveryJson),
        entity.SupportsTextToVideo,
        entity.SupportsImageToVideo);
}
