using System.Diagnostics;

namespace Pointframe.AutomationTests.Support;

public sealed class DesktopAcceptanceMetrics
{
    private readonly List<TimeSpan> _observationDurations = [];
    private readonly HashSet<string> _completedMatrixCells = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<TimeSpan> ObservationDurations => _observationDurations;

    public IReadOnlySet<string> CompletedMatrixCells => _completedMatrixCells;

    public void RecordObservation(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        _observationDurations.Add(duration);
    }

    public TimeSpan MeasureObservation(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var stopwatch = Stopwatch.StartNew();
        action();
        stopwatch.Stop();
        RecordObservation(stopwatch.Elapsed);
        return stopwatch.Elapsed;
    }

    public void MarkMatrixCell(string cell, bool executed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cell);
        if (executed)
        {
            _completedMatrixCells.Add(cell);
        }
    }

    public TimeSpan? ObservationP95()
    {
        if (_observationDurations.Count == 0)
        {
            return null;
        }

        var ordered = _observationDurations.OrderBy(value => value).ToArray();
        var index = Math.Clamp((int)Math.Ceiling(ordered.Length * 0.95) - 1, 0, ordered.Length - 1);
        return ordered[index];
    }
}
