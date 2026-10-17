using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WooVisAgent.Api.Extensions;
using WooVisAgentServices.ImageService.Commands;
using WooVisAgentServices.ImageService.Dtos;
using WooVisAgentServices.ImageService.Queries;
using WooVisAgentServices.VideoService.Commands;
using WooVisAgentServices.VideoService.Dtos;

namespace WooVisAgent.Api.Controllers;

/// <summary>
/// 视频生成接口，对应用例见 WooVisAgentServices.VideoService。
/// 前端调用方：WooVisAgent.Web/src/api/video.ts
///
/// 提交只负责"落库 + 投递消息到 RabbitMQ"，真正的生成由 WooVisAgent.GenerateWorker 完成，
/// 完成后再通过 SignalR 把状态与产物推回页面——与图片链路完全一致的异步架构。
///
/// 查询 / 取消 / 删除直接复用类型无关的通用用例（GetGenerationTasksQuery 等），
/// 它们已经支持按 type 过滤，因此视频任务列表不需要第二套查询逻辑。
/// </summary>
[ApiController]
[Route("api/video/generations")]
[Authorize]
[Produces("application/json")]
public class VideoController(ISender sender) : ControllerBase
{
    /// <summary>提交视频生成任务，返回任务 ID 与初始状态（产物由实时通道推送）</summary>
    [HttpPost]
    [ProducesResponseType<VideoGenerationResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Generate(
        [FromBody] VideoGenerationInput input,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(new SubmitVideoGenerationCommand(input), cancellationToken);
        return result.ToActionResult();
    }

    /// <summary>分页查询视频生成任务</summary>
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
        var query = new GetGenerationTasksQuery("video", status, keyword, modelId, page, pageSize);
        var result = await sender.Send(query, cancellationToken);
        return result.ToActionResult();
    }

    /// <summary>查询单条视频生成任务</summary>
    [HttpGet("{id:long}")]
    [ProducesResponseType<GenerationTaskDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTask(long id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new GetGenerationTaskDetailQuery(id), cancellationToken);
        return result.ToActionResult();
    }

    /// <summary>取消进行中的视频生成任务</summary>
    [HttpPost("{id:long}/cancel")]
    [ProducesResponseType<GenerationTaskDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Cancel(long id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new CancelGenerationTaskCommand(id), cancellationToken);
        return result.ToActionResult();
    }

    /// <summary>删除视频生成记录</summary>
    [HttpDelete("{id:long}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(long id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new DeleteGenerationTaskCommand(id), cancellationToken);
        return result.IsSuccess ? NoContent() : result.ToActionResult();
    }
}