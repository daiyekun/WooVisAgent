using WooVisAgent.Services.Common.Contracts;
using WooVisAgent.SharedLib.Domain;
using WooVisAgent.SharedLib.Messaging;
using WooVisAgent.SharedLib.Repository;
using WooVisAgent.SharedLib.Result;
using WooVisAgentServices.ModelService.Dtos;
using WooVisAgentServices.ModelService.Services;
using WooVisAgentServices.ModelService.Validation;

namespace WooVisAgentServices.ModelService.Commands;

/// <summary>对已保存的模型配置做连通性测试（密钥取自服务端，不经过前端）</summary>
public record TestModelConfigCommand(long Id) : ICommand<Result<ModelTestResultDto>>;

public class TestModelConfigCommandHandler(
    IRepository<ModelConfig> repository,
    ICurrentUser currentUser,
    IModelConnectivityTester tester) : ICommandHandler<TestModelConfigCommand, Result<ModelTestResultDto>>
{
    public async Task<Result<ModelTestResultDto>> Handle(
        TestModelConfigCommand command,
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

        var result = await tester.TestAsync(
            entity.Name,
            entity.Category,
            entity.Endpoint,
            entity.ApiKey,
            cancellationToken);

        return Result.Success(result);
    }
}

/// <summary>对尚未保存的表单草稿做连通性测试</summary>
public record TestModelDraftCommand(ModelConfigInput Input) : ICommand<Result<ModelTestResultDto>>;

public class TestModelDraftCommandHandler(
    ICurrentUser currentUser,
    IModelConnectivityTester tester) : ICommandHandler<TestModelDraftCommand, Result<ModelTestResultDto>>
{
    public async Task<Result<ModelTestResultDto>> Handle(
        TestModelDraftCommand command,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || string.IsNullOrEmpty(currentUser.Id))
            return Result.Unauthorized("未登录或登录状态已失效。");

        string? error = ModelConfigInputValidator.Validate(command.Input, out var category);
        if (error is not null)
            return Result.Invalid(error);

        var result = await tester.TestAsync(
            command.Input.Name,
            category,
            command.Input.Endpoint,
            command.Input.ApiKey,
            cancellationToken);

        return Result.Success(result);
    }
}
