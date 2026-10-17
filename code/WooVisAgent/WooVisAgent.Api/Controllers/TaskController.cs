using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WooVisAgent.Api.Extensions;
using WooVisAgentServices.ImageService.Commands;
using WooVisAgentServices.ImageService.Dtos;
using WooVisAgentServices.ImageService.Queries;

namespace WooVisAgent.Api.Controllers;

/// <summary>
/// 统一的任务接口，对应用例见 WooVisAgentServices.ImageService。
/// 前端调用方：WooVisAgent.Web/src/api/task.ts —— 任务列表页的数据来源。
/// 所有接口都需要登录，任务按当前用户隔离。
/// </summary>
[ApiController]
[Route("api/tasks")]
[Authorize]
[Produces("application/json")]
public class TaskController(ISender sender) : ControllerBase
{
    /// <summary>分页查询任务列表，可按类型 / 状态 / 关键词 / 模型筛选</summary>
    [HttpGet]
    [ProducesResponseType<PagedResultDto<GenerationTaskDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTasks(
        [FromQuery] string? type,
        [FromQuery] string? status,
        [FromQuery] string? keyword,
        [FromQuery] string? modelId,
        [FromQuery] int page,
        [FromQuery] int pageSize,
        CancellationToken cancellationToken)
    {
        var query = new GetGenerationTasksQuery(type, status, keyword, modelId, page, pageSize);
        var result = await sender.Send(query, cancellationToken);
        return result.ToActionResult();
    }

    /// <summary>查询任务详情（含输入素材与产物）</summary>
    [HttpGet("{id:long}")]
    [ProducesResponseType<GenerationTaskDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTask(long id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new GetGenerationTaskDetailQuery(id), cancellationToken);
        return result.ToActionResult();
    }

    /// <summary>查询任务产物</summary>
    [HttpGet("{id:long}/outputs")]
    [ProducesResponseType<IReadOnlyList<TaskAssetDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOutputs(long id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new GetGenerationTaskOutputsQuery(id), cancellationToken);
        return result.ToActionResult();
    }

    /// <summary>批量查询任务状态，前端在 SignalR 不可用时用它做兜底轮询</summary>
    [HttpPost("statuses")]
    [ProducesResponseType<IReadOnlyList<TaskStatusDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStatuses(
        [FromBody] TaskIdsRequest request,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(
            new GetGenerationTaskStatusesQuery(request.Ids ?? []),
            cancellationToken);

        return result.ToActionResult();
    }

    /// <summary>取消进行中的任务</summary>
    [HttpPost("{id:long}/cancel")]
    [ProducesResponseType<GenerationTaskDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Cancel(long id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new CancelGenerationTaskCommand(id), cancellationToken);
        return result.ToActionResult();
    }

    /// <summary>复用原任务参数重新提交</summary>
    [HttpPost("{id:long}/retry")]
    [ProducesResponseType<GenerationTaskDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Retry(long id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new RetryGenerationTaskCommand(id), cancellationToken);
        return result.ToActionResult();
    }

    /// <summary>
    /// 选秀：把已完成任务的产物收录进「我的作品」。
    /// 仅成功完成且尚未入选的任务可调用，重复调用会被后端拒绝。
    /// </summary>
    [HttpPost("{id:long}/select")]
    [ProducesResponseType<SelectGenerationTaskResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Select(long id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new SelectGenerationTaskCommand(id), cancellationToken);
        return result.ToActionResult();
    }

    /// <summary>批量删除任务</summary>
    [HttpPost("batch-delete")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> BatchDelete(
        [FromBody] TaskIdsRequest request,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(
            new BatchDeleteGenerationTasksCommand(request.Ids ?? []),
            cancellationToken);

        return result.IsSuccess ? NoContent() : result.ToActionResult();
    }

    /// <summary>删除单个任务</summary>
    [HttpDelete("{id:long}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(long id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new DeleteGenerationTaskCommand(id), cancellationToken);
        return result.IsSuccess ? NoContent() : result.ToActionResult();
    }

    /// <summary>批量操作的任务 ID 集合</summary>
    public record TaskIdsRequest
    {
        public List<string>? Ids { get; init; }
    }
}
