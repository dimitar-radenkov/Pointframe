namespace Pointframe.Data.Abstractions;

public interface IPointframeDataUnitOfWork : IUnitOfWork
{
    ICaptureTextCacheRepository CaptureTextCache { get; }
}
