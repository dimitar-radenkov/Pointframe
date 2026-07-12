using Microsoft.EntityFrameworkCore;
using Pointframe.Data.Abstractions;

namespace Pointframe.Data.Repository;

public abstract class Repository<TEntity> : ReadOnlyRepository<TEntity>, IRepository<TEntity>
    where TEntity : class, IEntity
{
    protected Repository(DbContext context)
        : base(context)
    {
    }

    public async Task<TEntity> Add(
        TEntity entity,
        CancellationToken cancellationToken = default)
    {
        await DbSet.AddAsync(entity, cancellationToken);
        return entity;
    }

    public Task Update(
        TEntity entity,
        CancellationToken cancellationToken = default)
    {
        DbSet.Update(entity);
        return Task.CompletedTask;
    }

    public Task Delete(
        TEntity entity,
        CancellationToken cancellationToken = default)
    {
        DbSet.Remove(entity);
        return Task.CompletedTask;
    }
}
