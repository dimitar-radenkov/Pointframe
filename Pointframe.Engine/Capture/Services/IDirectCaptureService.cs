namespace Pointframe.Engine;

public interface IDirectCaptureService
{
    string ListDisplays();

    string ListWindows();

    Task<string> CaptureMonitorAsync(string monitorName, CaptureRegion? region = null, CancellationToken cancellationToken = default);

    Task<string> CaptureMonitorTextAsync(string monitorName, CaptureRegion? region = null, CancellationToken cancellationToken = default);

    Task<string> CaptureWindowAsync(long windowId, CancellationToken cancellationToken = default);

    Task<string> CaptureWindowTextAsync(long windowId, CancellationToken cancellationToken = default);
}
