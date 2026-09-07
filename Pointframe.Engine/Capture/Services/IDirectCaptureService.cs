namespace Pointframe.Engine;

public interface IDirectCaptureService
{
    string ListDisplays();

    Task<string> CaptureMonitorAsync(string monitorName, CancellationToken cancellationToken = default);
}
