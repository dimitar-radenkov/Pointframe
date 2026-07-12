namespace Pointframe.Services;

public interface IOcrService
{
    Task<string?> Recognize(BitmapSource bitmap, CancellationToken cancellationToken = default);
}
