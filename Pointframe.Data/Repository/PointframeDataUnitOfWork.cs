using Pointframe.Data.Abstractions;
using Pointframe.Data.Context;

namespace Pointframe.Data.Repository;

internal sealed class PointframeDataUnitOfWork : UnitOfWork, IPointframeDataUnitOfWork
{
    private readonly PointframeDataContext _context;
    private ICaptureTextCacheRepository? _captureTextCache;
    private ICaptureCatalogRepository? _captureCatalog;

    public PointframeDataUnitOfWork(PointframeDataContext context)
        : base(context)
    {
        _context = context;
    }

    public ICaptureTextCacheRepository CaptureTextCache =>
        _captureTextCache ??= new CaptureTextCacheRepository(_context);

    public ICaptureCatalogRepository CaptureCatalog =>
        _captureCatalog ??= new CaptureCatalogRepository(_context);
}
