namespace Pointframe.Engine;

public interface IDirectCaptureService
{
    string ListDisplays();

    Task<string> CaptureMonitorAsync(string monitorName, CaptureRegion? region = null, CancellationToken cancellationToken = default);

    Task<string> CaptureMonitorTextAsync(string monitorName, CaptureRegion? region = null, CancellationToken cancellationToken = default);
}
