namespace Pointframe.Services;

internal sealed class NullTelemetryService : ITelemetryService
{
    public static NullTelemetryService Instance { get; } = new();

    private NullTelemetryService()
    {
    }

    public void TrackEvent(string name, IReadOnlyDictionary<string, string>? properties = null)
    {
    }

    public void TrackException(
        Exception exception,
        string? context = null,
        IReadOnlyDictionary<string, string>? properties = null)
    {
    }

    public void Flush()
    {
    }
}
