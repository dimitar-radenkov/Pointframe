using Pointframe.Engine.Automation.Models;

namespace Pointframe.Engine.Automation.Services;

public interface IDesktopObservationStore
{
    void Save(DesktopObservationResult result);

    DesktopObservationResult Resolve(string observationRef);

    DesktopObservedElement? ResolveElement(string elementRef, string processRef);

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

    public DesktopObservedElement? ResolveElement(string elementRef, string processRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(elementRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(processRef);
        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow();
            foreach (var expired in _items.Where(pair => pair.Value.ExpiresUtc <= now).Select(pair => pair.Key).ToArray())
            {
                _items.Remove(expired);
            }

            var latest = _items.Values
                .Select(item => item.Result)
                .Where(result => string.Equals(result.Observation.Process.ProcessRef, processRef, StringComparison.Ordinal))
                .OrderByDescending(result => result.Observation.PixelCapturedUtc)
                .FirstOrDefault();
            var element = latest?.UiAutomation?.Elements
                .FirstOrDefault(snapshot => string.Equals(snapshot.ElementRef, elementRef, StringComparison.Ordinal));
            return latest is null || element is null
                ? null
                : new DesktopObservedElement(latest.Observation.ObservationRef, element);
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
