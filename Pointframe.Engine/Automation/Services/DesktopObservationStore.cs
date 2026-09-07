using Pointframe.Engine.Automation.Models;

namespace Pointframe.Engine.Automation.Services;

public interface IDesktopObservationStore
{
    void Save(DesktopObservationResult result);

    DesktopObservationResult Resolve(string observationRef);

    void Invalidate(string observationRef);
}

public sealed class DesktopObservationStore(TimeProvider? timeProvider = null) : IDesktopObservationStore
{
    private readonly Dictionary<string, (DesktopObservationResult Result, DateTimeOffset ExpiresUtc)> _items = [];
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public void Save(DesktopObservationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_sync)
        {
            _items[result.Observation.ObservationRef] = (
                result,
                _timeProvider.GetUtcNow().AddSeconds(DesktopTestingLimits.ObservationTtlSeconds));
        }
    }

    public DesktopObservationResult Resolve(string observationRef)
    {
        lock (_sync)
        {
            if (!_items.TryGetValue(observationRef, out var item) || item.ExpiresUtc <= _timeProvider.GetUtcNow())
            {
                _items.Remove(observationRef);
                throw new DesktopOperationException("StaleObservation", $"Observation '{observationRef}' is stale or unknown.");
            }

            return item.Result;
        }
    }

    public void Invalidate(string observationRef)
    {
        lock (_sync)
        {
            _items.Remove(observationRef);
        }
    }
}

public sealed class DesktopOperationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
