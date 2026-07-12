namespace Pointframe.Services;

public interface ICaptureTextIndex
{
    Task<string?> GetText(CaptureItem item, CancellationToken cancellationToken = default);
}
