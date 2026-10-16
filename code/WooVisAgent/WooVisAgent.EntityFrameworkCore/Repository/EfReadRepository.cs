using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using WooVisAgent.SharedLib.Domain;
using WooVisAgent.SharedLib.Repository;


namespace WooVisAgent.EntityFrameworkCore.Repository;

public class EfReadRepository<T>(AiCopilotDbContext dbContext) : IReadRepository<T>
    where T : class, IAggregateRoot
{
    public IQueryable<T> GetQueryable()
    {
        return dbContext.Set<T>().AsQueryable();
    }

    public async Task<T?> GetByIdAsync<TKey>(TKey id, CancellationToken cancellationToken = default)
        where TKey : notnull
    {
        return await dbContext.Set<T>().FindAsync([id], cancellationToken);
    }

    public async Task<List<T>> GetListAsync(Expression<Func<T, bool>> expression,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.Set<T>().Where(expression).ToListAsync(cancellationToken);
    }

    public async Task<int> GetCountAsync(Expression<Func<T, bool>> expression,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.Set<T>().Where(expression).CountAsync(cancellationToken);
    }
    
    public async Task<T?> GetAsync(
        Expression<Func<T, bool>> expression, 
        Expression<Func<T, object>>[]? includes = null, 
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.Set<T>().AsQueryable();

        if (includes != null)
        {
            foreach (var include in includes)
            {
                query = query.Include(include);
            }
        }

        return await query.FirstOrDefaultAsync(expression, cancellationToken);
    }

    public async Task<List<T>> GetListAsync(
        Expression<Func<T, bool>> expression, 
        Expression<Func<T, object>>[]? includes = null, 
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.Set<T>().AsQueryable();

        if (includes != null)
        {
            foreach (var include in includes)
            {
                query = query.Include(include);
            }
        }

        return await query.Where(expression).ToListAsync(cancellationToken);
    }
}