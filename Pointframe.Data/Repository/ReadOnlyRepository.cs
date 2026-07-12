using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Pointframe.Data.Abstractions;

namespace Pointframe.Data.Repository;

public abstract class ReadOnlyRepository<TEntity> : IReadOnlyRepository<TEntity>
    where TEntity : class, IEntity
{
    protected readonly DbContext Context;
    protected readonly DbSet<TEntity> DbSet;

    protected ReadOnlyRepository(DbContext context)
    {
        Context = context;
        DbSet = context.Set<TEntity>();
    }

    public Task<TEntity?> FirstOrDefault(
        Expression<Func<TEntity, bool>> predicate,
        CancellationToken cancellationToken = default)
    {
        return DbSet.FirstOrDefaultAsync(predicate, cancellationToken);
    }

    public Task<bool> Exists(
        Expression<Func<TEntity, bool>> predicate,
        CancellationToken cancellationToken = default)
    {
        return DbSet.AnyAsync(predicate, cancellationToken);
    }

    public Task<int> Count(
        Expression<Func<TEntity, bool>>? predicate = null,
        CancellationToken cancellationToken = default)
    {
        return predicate is null
            ? DbSet.CountAsync(cancellationToken)
            : DbSet.CountAsync(predicate, cancellationToken);
    }
}
