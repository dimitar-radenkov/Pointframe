namespace Pointframe.Services;

internal readonly record struct SystemAudioSegment(int Offset, int Length);

internal sealed record SystemAudioPacketPlan(long SilenceBytesBefore, SystemAudioSegment Segment);

// Places loopback packets on the active (unpaused) recording timeline by the packet's own capture timestamp,
// not by when the callback ran. Silence fills real gaps (nothing playing, or audio that starts after the
// recording did), audio captured during a pause is trimmed away, and packets delivered late in a batch
// land at their own position instead of piling up behind a false gap. The clock and the packet timestamps
// share one time base (the QPC clock in production).
internal sealed class SystemAudioTimeline
{
    internal static readonly TimeSpan ContinuityBound = TimeSpan.FromMilliseconds(100);
    internal static readonly TimeSpan GapTolerance = TimeSpan.FromMilliseconds(20);

    private readonly int _bytesPerSecond;
    private readonly int _blockAlign;
    private readonly Func<TimeSpan> _clock;
    private readonly TimeSpan _start;
    private readonly List<(TimeSpan Start, TimeSpan End)> _pauses = [];
    private TimeSpan _pausedAt;
    private TimeSpan? _previousPacketEnd;

    public SystemAudioTimeline(int bytesPerSecond, int blockAlign, Func<TimeSpan> clock)
        : this(bytesPerSecond, blockAlign, clock, clock())
    {
    }

    // origin is the instant the video timeline starts, on the same clock as the packet timestamps.
    public SystemAudioTimeline(int bytesPerSecond, int blockAlign, Func<TimeSpan> clock, TimeSpan origin)
    {
        if (bytesPerSecond <= 0 || blockAlign <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytesPerSecond), "The audio format must have a positive byte rate and block size.");
        }

        _bytesPerSecond = bytesPerSecond;
        _blockAlign = blockAlign;
        _clock = clock;
        _start = origin;
    }

    public bool IsPaused { get; private set; }

    public long WrittenBytes { get; private set; }

    public TimeSpan ActiveElapsed => ActiveAt(IsPaused ? _pausedAt : _clock());

    public void Pause()
    {
        if (IsPaused)
        {
            return;
        }

        _pausedAt = _clock();
        IsPaused = true;
    }

    public void Resume()
    {
        if (!IsPaused)
        {
            return;
        }

        _pauses.Add((_pausedAt, _clock()));
        IsPaused = false;
    }

    // Plans what to write for one packet that was captured starting at packetStart. The plan is committed:
    // the caller must write the silence and then the segments, in order.
    // When timestampValid is false (WASAPI flagged the timestamp as uncertain) the reported time is not trusted: the packet
    // is placed right after the previous one if its arrival time says it is continuous (within ContinuityBound),
    // otherwise at its arrival time minus its duration. Pause trimming then applies as for any packet.
    public IReadOnlyList<SystemAudioPacketPlan> PlanPacket(TimeSpan packetStart, int packetBytes, bool timestampValid = true)
    {
        var plans = new List<SystemAudioPacketPlan>();
        if (packetBytes <= 0)
        {
            return plans;
        }

        if (!timestampValid)
        {
            var arrivalEstimate = _clock() - DurationOf(packetBytes);
            packetStart = _previousPacketEnd is { } previousEnd && (arrivalEstimate - previousEnd).Duration() <= ContinuityBound
                ? previousEnd
                : arrivalEstimate;
        }

        var packetEnd = packetStart + DurationOf(packetBytes);
        _previousPacketEnd = packetEnd;
        var cursor = packetStart > _start ? packetStart : _start;
        foreach (var (pauseStart, pauseEnd) in PauseIntervals())
        {
            if (cursor >= packetEnd)
            {
                break;
            }

            if (pauseStart > cursor)
            {
                AddSegment(plans, packetStart, packetBytes, cursor, pauseStart < packetEnd ? pauseStart : packetEnd);
            }

            if (pauseEnd > cursor)
            {
                cursor = pauseEnd;
            }
        }

        if (cursor < packetEnd)
        {
            AddSegment(plans, packetStart, packetBytes, cursor, packetEnd);
        }

        return plans;
    }

    public long SilenceBytesToFinish()
    {
        var silence = Math.Max(0, ExpectedBytes(ActiveElapsed) - WrittenBytes);
        WrittenBytes += silence;
        return silence;
    }

    private void AddSegment(List<SystemAudioPacketPlan> plans, TimeSpan packetStart, int packetBytes, TimeSpan from, TimeSpan to)
    {
        var offset = AlignDown(BytesOf(from - packetStart));
        var length = Math.Min(AlignDown(BytesOf(to - from)), packetBytes - offset);
        if (length <= 0)
        {
            return;
        }

        var gap = ExpectedBytes(ActiveAt(from)) - WrittenBytes;
        var silence = gap >= ExpectedBytes(GapTolerance) ? gap : 0;
        WrittenBytes += silence + length;
        plans.Add(new SystemAudioPacketPlan(silence, new SystemAudioSegment(offset, length)));
    }

    private IEnumerable<(TimeSpan Start, TimeSpan End)> PauseIntervals()
    {
        foreach (var pause in _pauses)
        {
            yield return pause;
        }

        if (IsPaused)
        {
            yield return (_pausedAt, TimeSpan.MaxValue);
        }
    }

    private TimeSpan ActiveAt(TimeSpan time)
    {
        var paused = TimeSpan.Zero;
        foreach (var (pauseStart, pauseEnd) in PauseIntervals())
        {
            var from = pauseStart > _start ? pauseStart : _start;
            var to = pauseEnd < time ? pauseEnd : time;
            if (to > from)
            {
                paused += to - from;
            }
        }

        var active = time - _start - paused;
        return active > TimeSpan.Zero ? active : TimeSpan.Zero;
    }

    private TimeSpan DurationOf(long bytes) => TimeSpan.FromSeconds((double)bytes / _bytesPerSecond);

    private long BytesOf(TimeSpan duration) => (long)Math.Round(duration.TotalSeconds * _bytesPerSecond);

    private int AlignDown(long bytes) => (int)(bytes - bytes % _blockAlign);

    private long ExpectedBytes(TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero)
        {
            return 0;
        }

        var bytes = (long)(elapsed.TotalSeconds * _bytesPerSecond);
        return bytes - bytes % _blockAlign;
    }
}
