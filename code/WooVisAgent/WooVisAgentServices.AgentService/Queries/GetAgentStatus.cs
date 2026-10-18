using WooVisAgent.Services.Common.Contracts;
using WooVisAgent.SharedLib.Messaging;
using WooVisAgent.SharedLib.Result;
using WooVisAgentServices.AgentService.Dtos;
using WooVisAgentServices.AgentService.Options;
using WooVisAgentServices.AgentService.Services;

namespace WooVisAgentServices.AgentService.Queries;

/// <summary>
/// 查询 Agent 是否可用。前端用它决定要不要显示「润色」入口，
/// 避免用户点了才发现没配语言模型。
/// </summary>
public record GetAgentStatusQuery : IQuery<Result<AgentStatusDto>>;

public class GetAgentStatusQueryHandler(
    ICurrentUser currentUser,
    ILlmClientResolver resolver,
    AgentOptions options) : IQueryHandler<GetAgentStatusQuery, Result<AgentStatusDto>>
{
    public async Task<Result<AgentStatusDto>> Handle(
        GetAgentStatusQuery query,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || string.IsNullOrEmpty(currentUser.Id))
            return Result.Unauthorized("未登录或登录状态已失效。");

        if (!options.Enabled)
            return Result.Success(new AgentStatusDto(false, false, null, null, "Agent 能力未启用。"));

        var resolution = await resolver.ResolveAsync(currentUser.Id, cancellationToken);

        return Result.Success(new AgentStatusDto(
            true,
            resolution.IsAvailable,
            resolution.Config?.Name,
            resolution.Config?.ModelKey,
            resolution.FailureReason));
    }
}
