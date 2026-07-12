using Microsoft.EntityFrameworkCore;
using Pointframe.Data.Abstractions;

namespace Pointframe.Data.Repository;

public abstract class UnitOfWork : IUnitOfWork
{
    private readonly DbContext _context;

    protected UnitOfWork(DbContext context)
    {
        _context = context;
    }

    public Task<int> SaveChanges(CancellationToken cancellationToken = default)
    {
        return _context.SaveChangesAsync(cancellationToken);
    }
}
