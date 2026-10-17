using WooVisAgent.Services.Common.Contracts;
using WooVisAgent.SharedLib.Domain;
using WooVisAgent.SharedLib.Messaging;
using WooVisAgent.SharedLib.Repository;
using WooVisAgent.SharedLib.Result;
using WooVisAgentServices.ModelService.Dtos;
using WooVisAgentServices.ModelService.Mapping;

namespace WooVisAgentServices.ModelService.Queries;

/// <summary>查询单条模型配置详情（仅限本人创建的配置）</summary>
public record GetModelConfigDetailQuery(long Id) : IQuery<Result<ModelConfigDto>>;

public class GetModelConfigDetailQueryHandler(
    IRepository<ModelConfig> repository,
    ICurrentUser currentUser) : IQueryHandler<GetModelConfigDetailQuery, Result<ModelConfigDto>>
{
    public async Task<Result<ModelConfigDto>> Handle(
        GetModelConfigDetailQuery query,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || string.IsNullOrEmpty(currentUser.Id))
            return Result.Unauthorized("未登录或登录状态已失效。");

        var ownerId = currentUser.Id;
        var entity = await repository.GetAsync(
            x => x.Id == query.Id && x.OwnerId == ownerId,
            cancellationToken: cancellationToken);

        if (entity is null)
            return Result.NotFound("模型配置不存在或无权访问。");

        // 详情接口只允许配置所有者访问；编辑页需要回填当前密钥。
        // 列表、新增、更新等其它响应仍不携带密钥，避免密钥在普通列表请求中扩散。
        return Result.Success(entity.ToDto(includeApiKey: true));
    }
}
