using Microsoft.EntityFrameworkCore;
using WooVisAgent.SharedLib.Domain;
using WooVisAgent.SharedLib.Repository;
using WooVisAgentServices.ModelService.Mapping;

namespace WooVisAgentServices.ModelService.Support;

/// <summary>模型配置的共用规则</summary>
internal static class ModelDefaultsHelper
{
    /// <summary>
    /// 同一用户、同一分类下只允许存在一个默认模型；
    /// 把除 <paramref name="exceptId" /> 之外的默认标记全部清掉。
    /// </summary>
    public static async Task ClearOtherDefaultsAsync(
        IRepository<ModelConfig> repository,
        string ownerId,
        ModelCategory category,
        long exceptId,
        CancellationToken cancellationToken)
    {
        var others = await repository.GetQueryable()
            .Where(x => x.OwnerId == ownerId
                        && x.Category == category
                        && x.Id != exceptId
                        && x.IsDefault)
            .ToListAsync(cancellationToken);

        if (others.Count == 0)
            return;

        var now = DateTimeOffset.UtcNow;
        foreach (var other in others)
        {
            other.IsDefault = false;
            other.UpdatedAt = now;
            repository.Update(other);
        }
    }
}
