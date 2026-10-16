using WooVisAgent.Services.Common.Contracts;
using WooVisAgent.SharedLib.Domain;
using WooVisAgent.SharedLib.Ids;
using WooVisAgent.SharedLib.Messaging;
using WooVisAgent.SharedLib.Repository;
using WooVisAgent.SharedLib.Result;
using WooVisAgentServices.ModelService.Dtos;
using WooVisAgentServices.ModelService.Mapping;
using WooVisAgentServices.ModelService.Serialization;
using WooVisAgentServices.ModelService.Support;
using WooVisAgentServices.ModelService.Validation;

namespace WooVisAgentServices.ModelService.Commands;

/// <summary>新增模型配置。主键由雪花算法生成（64 位）。</summary>
public record CreateModelConfigCommand(ModelConfigInput Input) : ICommand<Result<ModelConfigDto>>;

public class CreateModelConfigCommandHandler(
    IRepository<ModelConfig> repository,
    ICurrentUser currentUser,
    ISnowflakeIdGenerator idGenerator) : ICommandHandler<CreateModelConfigCommand, Result<ModelConfigDto>>
{
    public async Task<Result<ModelConfigDto>> Handle(
        CreateModelConfigCommand command,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || string.IsNullOrEmpty(currentUser.Id))
            return Result.Unauthorized("未登录或登录状态已失效。");

        string? error = ModelConfigInputValidator.Validate(command.Input, out var category);
        if (error is not null)
            return Result.Invalid(error);

        var input = command.Input;
        var ownerId = currentUser.Id;
        var now = DateTimeOffset.UtcNow;

        var entity = new ModelConfig
        {
            Id = idGenerator.NextId(),
            OwnerId = ownerId,
            Name = input.Name.Trim(),
            Provider = input.Provider.Trim(),
            Category = category,
            ModelKey = ModelConfigInputValidator.NormalizeOptional(input.ModelKey, 128),
            Endpoint = ModelConfigInputValidator.NormalizeEndpoint(input.Endpoint),
            ApiKey = ModelConfigInputValidator.NormalizeOptional(input.ApiKey, 1024),
            Enabled = input.Enabled,
            IsDefault = input.IsDefault,
            ParamsJson = ModelParamsSerializer.Serialize(input.Params),
            Remark = ModelConfigInputValidator.NormalizeOptional(input.Remark, 512),
            CreatedAt = now,
            UpdatedAt = now,
        };

        if (entity.IsDefault)
            await ModelDefaultsHelper.ClearOtherDefaultsAsync(repository, ownerId, category, entity.Id, cancellationToken);

        repository.Add(entity);
        await repository.SaveChangesAsync(cancellationToken);

        return Result.Success(entity.ToDto());
    }
}
