using System.Globalization;
using WooVisAgent.SharedLib.Domain;
using WooVisAgentServices.ImageService.Dtos;
using WooVisAgentServices.ImageService.Serialization;

namespace WooVisAgentServices.ImageService.Mapping;

/// <summary>
/// 会话与消息的出参映射。
///
/// 素材与参数反序列化的容错策略沿用 <see cref="GenerationTaskSerialization" />：
/// 单条脏数据不应该让整段聊天历史读不出来。
/// </summary>
public static class ConversationMapping
{
    public static ConversationDto ToDto(this Conversation entity) => new(
        entity.Id.ToString(CultureInfo.InvariantCulture),
        entity.Title,
        entity.Type.ToWireValue(),
        entity.MessageCount,
        entity.TaskCount,
        entity.CreatedAt,
        entity.UpdatedAt);

    public static ConversationMessageDto ToDto(this ConversationMessage entity) => new(
        entity.Id.ToString(CultureInfo.InvariantCulture),
        entity.Role,
        entity.Text,
        GenerationTaskSerialization.DeserializeAssets(entity.AssetsJson)
            .Select(x => x.ToDto()).ToList(),
        GenerationTaskSerialization.DeserializeParams(entity.ParamsJson),
        entity.TaskId?.ToString(CultureInfo.InvariantCulture),
        entity.PairMessageId,
        entity.SourceTaskId?.ToString(CultureInfo.InvariantCulture),
        entity.Seq,
        entity.ErrorMessage,
        entity.CreatedAt);
}
