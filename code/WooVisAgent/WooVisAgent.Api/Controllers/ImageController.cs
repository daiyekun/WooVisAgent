using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WooVisAgent.Api.Extensions;
using WooVisAgentServices.ImageService.Commands;
using WooVisAgentServices.ImageService.Dtos;
using WooVisAgentServices.ImageService.Queries;

namespace WooVisAgent.Api.Controllers;

/// <summary>
/// 图片生成接口，对应用例见 WooVisAgentServices.ImageService。
/// 前端调用方：WooVisAgent.Web/src/api/image.ts
///
/// 提交只负责"落库 + 投递消息到 RabbitMQ"，真正的生成由 WooVisAgent.GenerateWorker 完成，
/// 完成后再通过 SignalR 把状态与产物推回页面。
/// </summary>
[ApiController]
[Route("api/image/generations")]
[Authorize]
[Produces("application/json")]
public class ImageController(ISender sender) : ControllerBase
{
    /// <summary>提交图片生成任务，返回任务 ID 与初始状态（产物由实时通道推送）</summary>
    [HttpPost]
    [ProducesResponseType<ImageGenerationResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Generate(
        [FromBody] ImageGenerationInput input,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(new SubmitImageGenerationCommand(input), cancellationToken);
        return result.ToActionResult();
    }

    /// <summary>分页查询图片生成任务</summary>
    [HttpGet]
    [ProducesResponseType<PagedResultDto<GenerationTaskDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTasks(
        [FromQuery] string? status,
        [FromQuery] string? keyword,
        [FromQuery] string? modelId,
        [FromQuery] int page,
        [FromQuery] int pageSize,
        CancellationToken cancellationToken)
    {
        var query = new GetGenerationTasksQuery("image", status, keyword, modelId, page, pageSize);
        var result = await sender.Send(query, cancellationToken);
        return result.ToActionResult();
    }

    /// <summary>查询单条图片生成任务</summary>
    [HttpGet("{id:long}")]
    [ProducesResponseType<GenerationTaskDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTask(long id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new GetGenerationTaskDetailQuery(id), cancellationToken);
        return result.ToActionResult();
    }

    /// <summary>取消进行中的图片生成任务</summary>
    [HttpPost("{id:long}/cancel")]
    [ProducesResponseType<GenerationTaskDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Cancel(long id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new CancelGenerationTaskCommand(id), cancellationToken);
        return result.ToActionResult();
    }

    /// <summary>删除图片生成记录</summary>
    [HttpDelete("{id:long}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(long id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new DeleteGenerationTaskCommand(id), cancellationToken);
        return result.IsSuccess ? NoContent() : result.ToActionResult();
    }
}
