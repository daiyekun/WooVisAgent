using WooVisAgent.Services.Common.Contracts;
using WooVisAgent.SharedLib.Domain;
using WooVisAgent.SharedLib.Messaging;
using WooVisAgent.SharedLib.Repository;
using WooVisAgent.SharedLib.Result;
using WooVisAgentServices.ModelService.Dtos;
using WooVisAgentServices.ModelService.Mapping;
using WooVisAgentServices.ModelService.Support;

namespace WooVisAgentServices.ModelService.Commands;

/// <summary>把模型设为所在分类的默认模型（同分类内互斥）</summary>
public record SetDefaultModelConfigCommand(long Id) : ICommand<Result<ModelConfigDto>>;

public class SetDefaultModelConfigCommandHandler(
    IRepository<ModelConfig> repository,
    ICurrentUser currentUser) : ICommandHandler<SetDefaultModelConfigCommand, Result<ModelConfigDto>>
{
    public async Task<Result<ModelConfigDto>> Handle(
        SetDefaultModelConfigCommand command,
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

        if (!entity.Enabled)
            return Result.Invalid("停用的模型不能设为默认模型，请先启用。");

        await ModelDefaultsHelper.ClearOtherDefaultsAsync(
            repository, ownerId, entity.Category, entity.Id, cancellationToken);

        entity.IsDefault = true;
        entity.UpdatedAt = DateTimeOffset.UtcNow;

        repository.Update(entity);
        await repository.SaveChangesAsync(cancellationToken);

        return Result.Success(entity.ToDto());
    }
}
