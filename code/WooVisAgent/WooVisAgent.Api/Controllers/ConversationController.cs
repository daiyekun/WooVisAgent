using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WooVisAgent.Api.Extensions;
using WooVisAgentServices.ImageService.Commands;
using WooVisAgentServices.ImageService.Dtos;
using WooVisAgentServices.ImageService.Queries;

namespace WooVisAgent.Api.Controllers;

/// <summary>
/// 创作会话接口。
/// 前端调用方：WooVisAgent.Web/src/api/conversation.ts —— 侧边栏「最近会话」、
/// 「更多会话」列表页、以及聊天页的历史回放都走这里。
///
/// 会话与任务是 1:N：每次提交生成都会在该会话下追加一对消息并新建一个任务，
/// 所以「一次会话多个任务」是天然成立的，不需要额外的关联表。
/// </summary>
[ApiController]
[Route("api/conversations")]
[Authorize]
[Produces("application/json")]
public class ConversationController(ISender sender) : ControllerBase
{
    /// <summary>分页查询会话列表，可按类型（图片 / 视频）与关键词筛选</summary>
    [HttpGet]
    [ProducesResponseType<PagedResultDto<ConversationDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetConversations(
        [FromQuery] string? type,
        [FromQuery] string? keyword,
        [FromQuery] int page,
        [FromQuery] int pageSize,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(
            new GetConversationsQuery(type, keyword, page, pageSize), cancellationToken);

        return result.ToActionResult();
    }

    /// <summary>会话总数与图片 / 视频分类计数，供侧边栏「更多会话(N)」与分类页签使用</summary>
    [HttpGet("summary")]
    [ProducesResponseType<ConversationSummaryDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSummary(CancellationToken cancellationToken)
    {
        var result = await sender.Send(new GetConversationSummaryQuery(), cancellationToken);
        return result.ToActionResult();
    }

    /// <summary>取（或复用）一个空会话，聊天页进入时先拿 id，首条消息提交后才有归属</summary>
    [HttpPost("ensure")]
    [ProducesResponseType<ConversationDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Ensure(CancellationToken cancellationToken)
    {
        var result = await sender.Send(new EnsureConversationCommand(), cancellationToken);
        return result.ToActionResult();
    }

    /// <summary>
    /// 会话详情：会话 + 全部消息 + 该会话下的任务。
    /// <paramref name="highlightTaskId" /> 不返回给前端语义，只用于让前端知道该往哪滚。
    /// </summary>
    [HttpGet("{id:long}")]
    [ProducesResponseType<ConversationDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDetail(
        long id,
        [FromQuery] long? highlightTaskId,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(
            new GetConversationDetailQuery(id, highlightTaskId), cancellationToken);

        return result.ToActionResult();
    }

    /// <summary>新建会话</summary>
    [HttpPost]
    [ProducesResponseType<ConversationDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Create(
        [FromBody] ConversationRequest? request,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(
            new CreateConversationCommand(request?.Title), cancellationToken);

        return result.ToActionResult();
    }

    /// <summary>改会话标题</summary>
    [HttpPut("{id:long}")]
    [ProducesResponseType<ConversationDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Rename(
        long id,
        [FromBody] ConversationRequest request,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(
            new RenameConversationCommand(id, request.Title ?? string.Empty), cancellationToken);

        return result.ToActionResult();
    }

    /// <summary>
    /// 删除会话，并级联删除其下的消息与任务。
    /// 已选秀进「我的作品」的产物会保留，返回值里给出影响面供前端提示。
    /// </summary>
    [HttpDelete("{id:long}")]
    [ProducesResponseType<DeleteConversationResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(long id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new DeleteConversationCommand(id), cancellationToken);
        return result.ToActionResult();
    }

    /// <summary>会话入参</summary>
    public record ConversationRequest
    {
        public string? Title { get; init; }
    }
}
