using WooVisAgent.Services.Common.Contracts;
using WooVisAgent.SharedLib.Domain;
using WooVisAgent.SharedLib.Messaging;
using WooVisAgent.SharedLib.Repository;
using WooVisAgent.SharedLib.Result;
using WooVisAgentServices.ModelService.Dtos;
using WooVisAgentServices.ModelService.Mapping;

namespace WooVisAgentServices.ModelService.Commands;

/// <summary>
/// 启用 / 停用模型配置。
/// 停用时会一并取消「默认模型」标记——停用的模型不应再被新建任务自动选中。
/// </summary>
public record ToggleModelConfigEnabledCommand(long Id, bool Enabled) : ICommand<Result<ModelConfigDto>>;

public class ToggleModelConfigEnabledCommandHandler(
    IRepository<ModelConfig> repository,
    ICurrentUser currentUser) : ICommandHandler<ToggleModelConfigEnabledCommand, Result<ModelConfigDto>>
{
    public async Task<Result<ModelConfigDto>> Handle(
        ToggleModelConfigEnabledCommand command,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || string.IsNullOrEmpty(currentUser.Id))
            return Result.Unauthorized("未登录或登录状态已失效。");

        var ownerId = currentUser.Id;
        var entity = await repository.GetAsync(
            x => x.Id == command.Id && x.OwnerId == ownerId,
            cancellationToken: cancellationToken);

        if (entity is null)
            return Result.NotFound("模型配置不存在或无权访问。");

        entity.Enabled = command.Enabled;
        if (!command.Enabled)
            entity.IsDefault = false;

        entity.UpdatedAt = DateTimeOffset.UtcNow;

        repository.Update(entity);
        await repository.SaveChangesAsync(cancellationToken);

        return Result.Success(entity.ToDto());
    }
}
