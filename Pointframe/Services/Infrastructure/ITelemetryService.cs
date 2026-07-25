namespace Pointframe.Services;

public interface ITelemetryService
{
    void TrackProductEvent(string name, IReadOnlyDictionary<string, string>? properties = null);

    void TrackDiagnosticEvent(string name, IReadOnlyDictionary<string, string>? properties = null);

    void TrackDiagnosticException(
        Exception exception,
        string? context = null,
        IReadOnlyDictionary<string, string>? properties = null);

    void TrackEvent(string name, IReadOnlyDictionary<string, string>? properties = null);

    void TrackException(Exception exception, string? context = null);

    void Flush();
}
