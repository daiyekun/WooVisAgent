using WooVisAgent.SharedLib.Domain;
using WooVisAgent.SharedLib.Repository;


namespace WooVisAgent.EntityFrameworkCore.Repository;

public class EfRepository<T>(AiCopilotDbContext dbContext) : EfReadRepository<T>(dbContext), IRepository<T>
    where T : class, IEntity, IAggregateRoot
{
    private readonly AiCopilotDbContext _dbContext = dbContext;

    public T Add(T entity)
    {
        _dbContext.Set<T>().Add(entity);
        return entity;
    }

    public void Update(T entity)
    {
        _dbContext.Set<T>().Update(entity);
    }

    public void Delete(T entity)
    {
        _dbContext.Set<T>().Remove(entity);
    }

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return await _dbContext.SaveChangesAsync(cancellationToken);
    }
}