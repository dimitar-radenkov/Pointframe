namespace Pointframe.Services;

public interface ITelemetryService
{
    void TrackEvent(string name, IReadOnlyDictionary<string, string>? properties = null);

    void TrackException(Exception exception, string? context = null);

    void Flush();
}
