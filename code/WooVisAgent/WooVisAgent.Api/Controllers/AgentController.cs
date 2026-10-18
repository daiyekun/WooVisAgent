using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WooVisAgent.Api.Extensions;
using WooVisAgentServices.AgentService.Commands;
using WooVisAgentServices.AgentService.Dtos;
using WooVisAgentServices.AgentService.Queries;

namespace WooVisAgent.Api.Controllers;

/// <summary>
/// Agent 接口（Microsoft Agent Framework）。
/// 前端调用方：WooVisAgent.Web/src/api/agent.ts
///
/// 边界：这里只做「理解意图 / 改写提示词 / 推荐参数」，
/// **不提交任何生成任务**，也不参与进度轮询与产物落盘。
/// </summary>
[ApiController]
[Route("api/agent")]
[Authorize]
[Produces("application/json")]
public class AgentController(ISender sender) : ControllerBase
{
    /// <summary>Agent 是否可用（有没有配可用的语言模型）</summary>
    [HttpGet("status")]
    [ProducesResponseType<AgentStatusDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStatus(CancellationToken cancellationToken)
    {
        var result = await sender.Send(new GetAgentStatusQuery(), cancellationToken);
        return result.ToActionResult();
    }

    /// <summary>提示词润色</summary>
    [HttpPost("polish")]
    [ProducesResponseType<PromptPolishDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Polish(
        [FromBody] PromptPolishRequest request,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(new PolishPromptCommand(request), cancellationToken);
        return result.ToActionResult();
    }

    /// <summary>一句话需求 → 视频参数建议（只建议，不提交）</summary>
    [HttpPost("video-plan")]
    [ProducesResponseType<VideoPlanDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> PlanVideo(
        [FromBody] VideoPlanRequest request,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(new PlanVideoCommand(request), cancellationToken);
        return result.ToActionResult();
    }
}
