namespace Pointframe.Services;

public interface ICaptureTextLookupService
{
    Task<string?> GetText(CaptureItem item, CancellationToken cancellationToken = default);
}
