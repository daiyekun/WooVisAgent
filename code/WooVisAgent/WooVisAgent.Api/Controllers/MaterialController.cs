using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WooVisAgent.Api.Extensions;
using WooVisAgentServices.ImageService.Commands;
using WooVisAgentServices.ImageService.Dtos;
using WooVisAgentServices.ImageService.Queries;

namespace WooVisAgent.Api.Controllers;

/// <summary>
/// 作品库（我的产品）接口。
/// 前端调用方：WooVisAgent.Web/src/api/material.ts —— 作品列表页的数据来源。
///
/// 作品有两个来源：在任务列表点「选秀」自动收录，或由 <c>SelectGenerationTaskCommand</c> 写入。
/// 本控制器只负责读取与删除，写入统一走任务侧的选秀接口，避免两条路径产生不一致的数据。
/// </summary>
[ApiController]
[Route("api/materials")]
[Authorize]
[Produces("application/json")]
public class MaterialController(ISender sender) : ControllerBase
{
    /// <summary>分页查询作品列表，可按类型 / 分组 / 关键词筛选</summary>
    [HttpGet]
    [ProducesResponseType<PagedResultDto<MaterialAssetDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMaterials(
        [FromQuery] string? type,
        [FromQuery] string? groupName,
        [FromQuery] string? keyword,
        [FromQuery] int page,
        [FromQuery] int pageSize,
        CancellationToken cancellationToken)
    {
        var query = new GetMaterialAssetsQuery(type, groupName, keyword, page, pageSize);
        var result = await sender.Send(query, cancellationToken);
        return result.ToActionResult();
    }

    /// <summary>作品分组列表，供前端渲染筛选页签</summary>
    [HttpGet("groups")]
    [ProducesResponseType<IReadOnlyList<string>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetGroups(CancellationToken cancellationToken)
    {
        var result = await sender.Send(new GetMaterialGroupsQuery(), cancellationToken);
        return result.ToActionResult();
    }

    /// <summary>删除单个作品</summary>
    [HttpDelete("{id:long}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(long id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(
            new DeleteMaterialAssetCommand(id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            cancellationToken);

        return result.IsSuccess ? NoContent() : result.ToActionResult();
    }

    /// <summary>批量删除作品</summary>
    [HttpPost("batch-delete")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> BatchDelete(
        [FromBody] MaterialIdsRequest request,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(
            new BatchDeleteMaterialAssetsCommand(request.Ids ?? []),
            cancellationToken);

        return result.IsSuccess ? NoContent() : result.ToActionResult();
    }

    /// <summary>批量操作的作品 ID 集合</summary>
    public record MaterialIdsRequest
    {
        public List<string>? Ids { get; init; }
    }
}