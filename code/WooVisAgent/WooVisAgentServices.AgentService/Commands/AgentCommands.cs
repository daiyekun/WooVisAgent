using WooVisAgent.Services.Common.Contracts;
using WooVisAgent.SharedLib.Messaging;
using WooVisAgent.SharedLib.Result;
using WooVisAgentServices.AgentService.Dtos;
using WooVisAgentServices.AgentService.Services;

namespace WooVisAgentServices.AgentService.Commands;

/// <summary>提示词润色：把一句口语描述改写成更贴合生成模型的提示词</summary>
public record PolishPromptCommand(PromptPolishRequest Request) : ICommand<Result<PromptPolishDto>>;

public class PolishPromptCommandHandler(
    ICurrentUser currentUser,
    IWooVisDirector director) : ICommandHandler<PolishPromptCommand, Result<PromptPolishDto>>
{
    public async Task<Result<PromptPolishDto>> Handle(
        PolishPromptCommand command,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || string.IsNullOrEmpty(currentUser.Id))
            return Result.Unauthorized("未登录或登录状态已失效。");

        var outcome = await director.PolishAsync(currentUser.Id, command.Request, cancellationToken);
        return outcome.IsSuccess
            ? Result.Success(outcome.Value!)
            : Result.Failure(outcome.Error ?? "提示词润色失败。");
    }
}

/// <summary>
/// 视频参数规划：一句话需求 → 可直接提交的参数建议。
/// 只返回建议，不创建任务。
/// </summary>
public record PlanVideoCommand(VideoPlanRequest Request) : ICommand<Result<VideoPlanDto>>;

public class PlanVideoCommandHandler(
    ICurrentUser currentUser,
    IWooVisDirector director) : ICommandHandler<PlanVideoCommand, Result<VideoPlanDto>>
{
    public async Task<Result<VideoPlanDto>> Handle(
        PlanVideoCommand command,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || string.IsNullOrEmpty(currentUser.Id))
            return Result.Unauthorized("未登录或登录状态已失效。");

        var outcome = await director.PlanVideoAsync(currentUser.Id, command.Request, cancellationToken);
        return outcome.IsSuccess
            ? Result.Success(outcome.Value!)
            : Result.Failure(outcome.Error ?? "视频参数规划失败。");
    }
}
