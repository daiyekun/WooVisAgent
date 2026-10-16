using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WooVisAgent.Api.Extensions;
using WooVisAgentServices.ModelService.Commands;
using WooVisAgentServices.ModelService.Dtos;
using WooVisAgentServices.ModelService.Providers;
using WooVisAgentServices.ModelService.Queries;

namespace WooVisAgent.Api.Controllers;

/// <summary>
/// 模型配置接口，对应用例见 WooVisAgentServices.ModelService。
/// 前端调用方：WooVisAgent.Web/src/api/model.ts
/// 所有接口都需要登录，配置按当前用户隔离。
/// </summary>
[ApiController]
[Route("api/models")]
[Authorize]
[Produces("application/json")]
public class ModelController(ISender sender, IModelProviderCatalog providerCatalog) : ControllerBase
{
    /// <summary>查询模型配置列表，category 可选（image / video / llm）</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<ModelConfigDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetModels(
        [FromQuery] string? category,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(new GetModelConfigsQuery(category), cancellationToken);
        return result.ToActionResult();
    }

    /// <summary>查询单条模型配置</summary>
    [HttpGet("{id:long}")]
    [ProducesResponseType<ModelConfigDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetModel(long id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new GetModelConfigDetailQuery(id), cancellationToken);
        return result.ToActionResult();
    }

    /// <summary>新增模型配置，主键由服务端生成（64 位雪花 ID）</summary>
    [HttpPost]
    [ProducesResponseType<ModelConfigDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateModel(
        [FromBody] ModelConfigInput input,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(new CreateModelConfigCommand(input), cancellationToken);
        return result.ToActionResult();
    }

    /// <summary>更新模型配置；apiKey 留空表示沿用原密钥</summary>
    [HttpPut("{id:long}")]
    [ProducesResponseType<ModelConfigDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateModel(
        long id,
        [FromBody] ModelConfigInput input,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(new UpdateModelConfigCommand(id, input), cancellationToken);
        return result.ToActionResult();
    }

    /// <summary>删除模型配置</summary>
    [HttpDelete("{id:long}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteModel(long id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new DeleteModelConfigCommand(id), cancellationToken);
        return result.IsSuccess ? NoContent() : result.ToActionResult();
    }

    /// <summary>启用 / 停用模型配置</summary>
    [HttpPut("{id:long}/enabled")]
    [ProducesResponseType<ModelConfigDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ToggleEnabled(
        long id,
        [FromBody] ToggleEnabledRequest request,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(
            new ToggleModelConfigEnabledCommand(id, request.Enabled),
            cancellationToken);

        return result.ToActionResult();
    }

    /// <summary>设为该分类的默认模型（同分类内互斥）</summary>
    [HttpPost("{id:long}/default")]
    [ProducesResponseType<ModelConfigDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetDefault(long id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new SetDefaultModelConfigCommand(id), cancellationToken);
        return result.ToActionResult();
    }

    /// <summary>连通性测试：本地校验 + 对接口地址发一次轻量探测</summary>
    [HttpPost("{id:long}/test")]
    [ProducesResponseType<ModelTestResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> TestModel(long id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new TestModelConfigCommand(id), cancellationToken);
        return result.ToActionResult();
    }

    /// <summary>未落库的新配置试连</summary>
    [HttpPost("test")]
    [ProducesResponseType<ModelTestResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> TestModelDraft(
        [FromBody] ModelConfigInput input,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(new TestModelDraftCommand(input), cancellationToken);
        return result.ToActionResult();
    }

    /// <summary>拉取指定供应方支持的模型清单</summary>
    [HttpGet("providers/{provider}/models")]
    [ProducesResponseType<IReadOnlyList<ProviderModelDto>>(StatusCodes.Status200OK)]
    public IActionResult GetProviderModels(string provider)
        => Ok(providerCatalog.GetModels(provider));

    public record ToggleEnabledRequest
    {
        public bool Enabled { get; init; }
    }
}
