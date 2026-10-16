using WooVisAgent.Services.Common.Contracts;
using WooVisAgent.SharedLib.Domain;
using WooVisAgent.SharedLib.Messaging;
using WooVisAgent.SharedLib.Repository;
using WooVisAgent.SharedLib.Result;
using WooVisAgentServices.ModelService.Dtos;
using WooVisAgentServices.ModelService.Mapping;
using WooVisAgentServices.ModelService.Serialization;
using WooVisAgentServices.ModelService.Support;
using WooVisAgentServices.ModelService.Validation;

namespace WooVisAgentServices.ModelService.Commands;

/// <summary>
/// 更新模型配置。分类会随编辑一起变更，因此把「默认模型」的新旧分类都做一次互斥处理。
/// </summary>
public record UpdateModelConfigCommand(long Id, ModelConfigInput Input) : ICommand<Result<ModelConfigDto>>;

public class UpdateModelConfigCommandHandler(
    IRepository<ModelConfig> repository,
    ICurrentUser currentUser) : ICommandHandler<UpdateModelConfigCommand, Result<ModelConfigDto>>
{
    public async Task<Result<ModelConfigDto>> Handle(
        UpdateModelConfigCommand command,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || string.IsNullOrEmpty(currentUser.Id))
            return Result.Unauthorized("未登录或登录状态已失效。");

        string? error = ModelConfigInputValidator.Validate(command.Input, out var category);
        if (error is not null)
            return Result.Invalid(error);

        var ownerId = currentUser.Id;
        var entity = await repository.GetAsync(
            x => x.Id == command.Id && x.OwnerId == ownerId,
            cancellationToken: cancellationToken);

        if (entity is null)
            return Result.NotFound("模型配置不存在或无权访问。");

        var input = command.Input;
        var previousCategory = entity.Category;
        var now = DateTimeOffset.UtcNow;

        entity.Name = input.Name.Trim();
        entity.Provider = input.Provider.Trim();
        entity.Category = category;
        entity.ModelKey = ModelConfigInputValidator.NormalizeOptional(input.ModelKey, 128);
        entity.Endpoint = ModelConfigInputValidator.NormalizeEndpoint(input.Endpoint);
        entity.Enabled = input.Enabled;
        entity.IsDefault = input.IsDefault;
        entity.ParamsJson = ModelParamsSerializer.Serialize(input.Params);
        entity.Remark = ModelConfigInputValidator.NormalizeOptional(input.Remark, 512);
        entity.UpdatedAt = now;

        // 密钥留空（或未提交该字段）表示沿用原密钥
        if (!string.IsNullOrWhiteSpace(input.ApiKey))
            entity.ApiKey = ModelConfigInputValidator.NormalizeOptional(input.ApiKey, 1024);

        if (entity.IsDefault)
            await ModelDefaultsHelper.ClearOtherDefaultsAsync(repository, ownerId, category, entity.Id, cancellationToken);

        // 分类被改走时，原分类下不能留下一个指向别的分类的默认模型
        if (previousCategory != category)
            await ModelDefaultsHelper.ClearOtherDefaultsAsync(
                repository, ownerId, previousCategory, entity.Id, cancellationToken);

        repository.Update(entity);
        await repository.SaveChangesAsync(cancellationToken);

        return Result.Success(entity.ToDto());
    }
}
