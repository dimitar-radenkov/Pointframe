namespace Pointframe.Data.Abstractions;

public interface IRepository<TEntity> : IReadOnlyRepository<TEntity>
    where TEntity : class, IEntity
{
    Task<TEntity> Add(
        TEntity entity,
        CancellationToken cancellationToken = default);

    Task Update(
        TEntity entity,
        CancellationToken cancellationToken = default);

    Task Delete(
        TEntity entity,
        CancellationToken cancellationToken = default);
}
