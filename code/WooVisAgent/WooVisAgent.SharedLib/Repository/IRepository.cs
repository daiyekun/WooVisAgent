using WooVisAgent.SharedLib.Domain;

namespace WooVisAgent.SharedLib.Repository;

/// <summary>
///     <para>
///         <see cref="IRepository{T}" /> 用于查询和保存 <typeparamref name="T" />
///     </para>
/// </summary>
/// <typeparam name="T">该仓储操作的实体类型</typeparam>
public interface IRepository<T> : IReadRepository<T> where T : class, IEntity, IAggregateRoot
{
    /// <summary>
    ///     在数据库中添加实体
    /// </summary>
    /// <param name="entity">要添加的实体</param>
    /// <returns></returns>
    T Add(T entity);

    /// <summary>
    ///     在数据库中更新实体
    /// </summary>
    /// <param name="entity">要更新的实体</param>
    /// <returns></returns>
    void Update(T entity);

    /// <summary>
    ///     在数据库中删除实体
    /// </summary>
    /// <param name="entity">要删除的实体</param>
    /// <returns></returns>
    void Delete(T entity);

    /// <summary>
    ///     持久化实体到数据库
    /// </summary>
    /// <returns></returns>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}