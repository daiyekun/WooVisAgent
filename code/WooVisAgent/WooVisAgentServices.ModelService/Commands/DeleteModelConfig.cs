using WooVisAgent.Services.Common.Contracts;
using WooVisAgent.SharedLib.Domain;
using WooVisAgent.SharedLib.Messaging;
using WooVisAgent.SharedLib.Repository;
using WooVisAgent.SharedLib.Result;

namespace WooVisAgentServices.ModelService.Commands;

/// <summary>删除模型配置</summary>
public record DeleteModelConfigCommand(long Id) : ICommand<Result>;

public class DeleteModelConfigCommandHandler(
    IRepository<ModelConfig> repository,
    ICurrentUser currentUser) : ICommandHandler<DeleteModelConfigCommand, Result>
{
    public async Task<Result> Handle(DeleteModelConfigCommand command, CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || string.IsNullOrEmpty(currentUser.Id))
            return Result.Unauthorized("未登录或登录状态已失效。");

        var ownerId = currentUser.Id;
        var entity = await repository.GetAsync(
            x => x.Id == command.Id && x.OwnerId == ownerId,
            cancellationToken: cancellationToken);

        if (entity is null)
            return Result.NotFound("模型配置不存在或无权访问。");

        repository.Delete(entity);
        await repository.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
