using System.Linq.Expressions;

namespace Pointframe.Data.Abstractions;

public interface IReadOnlyRepository<TEntity>
    where TEntity : class, IEntity
{
    Task<TEntity?> FirstOrDefault(
        Expression<Func<TEntity, bool>> predicate,
        CancellationToken cancellationToken = default);

    Task<bool> Exists(
        Expression<Func<TEntity, bool>> predicate,
        CancellationToken cancellationToken = default);

    Task<int> Count(
        Expression<Func<TEntity, bool>>? predicate = null,
        CancellationToken cancellationToken = default);
}
