using System.Linq.Expressions;
using WooVisAgent.SharedLib.Domain;

namespace WooVisAgent.SharedLib.Repository;

/// <summary>
///     <para>
///         <see cref="T" /> 用于查询 <typeparamref name="T" />.
///     </para>
/// </summary>
/// <typeparam name="T">该仓储操作的实体类型</typeparam>
public interface IReadRepository<T> where T : class, IAggregateRoot
{
    /// <summary>
    ///     获取 Queryable 查询表达式
    /// </summary>
    /// <returns></returns>
    IQueryable<T> GetQueryable();

    /// <summary>
    ///     查询具有指定主键的实体
    /// </summary>
    /// <typeparam name="TKey">主键的类型</typeparam>
    /// <param name="id">要查找的实体的主键值</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<T?> GetByIdAsync<TKey>(TKey id, CancellationToken cancellationToken = default);
    
    /// <summary>
    ///     查询实体集合
    /// </summary>
    /// <param name="expression"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<List<T>> GetListAsync(Expression<Func<T, bool>> expression, CancellationToken cancellationToken = default);

    /// <summary>
    ///     统计符合条件的记录总数
    /// </summary>
    /// <param name="expression"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<int> GetCountAsync(Expression<Func<T, bool>> expression, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// 根据条件获取单条实体，并包含指定的导航属性
    /// </summary>
    /// <param name="expression">查询条件</param>
    /// <param name="includes">需要加载的导航属性表达式数组</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<T?> GetAsync(
        Expression<Func<T, bool>> expression, 
        Expression<Func<T, object>>[]? includes = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 根据条件获取实体列表，并包含指定的导航属性
    /// </summary>
    /// <param name="expression">查询条件</param>
    /// <param name="includes">需要加载的导航属性表达式数组</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<List<T>> GetListAsync(
        Expression<Func<T, bool>> expression, 
        Expression<Func<T, object>>[]? includes = null, 
        CancellationToken cancellationToken = default);
}