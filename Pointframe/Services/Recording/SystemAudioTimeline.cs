namespace Pointframe.Services;

// Keeps the loopback WAV the same length as the active (unpaused) video timeline.
// Loopback delivers no data while nothing plays, so the gaps are filled with silence,
// and data that arrives while the recording is paused is dropped.
internal sealed class SystemAudioTimeline
{
    internal static readonly TimeSpan GapTolerance = TimeSpan.FromMilliseconds(100);

    private readonly int _bytesPerSecond;
    private readonly int _blockAlign;
    private readonly Func<TimeSpan> _clock;
    private readonly TimeSpan _start;
    private TimeSpan _pausedTotal;
    private TimeSpan _pausedAt;

    public SystemAudioTimeline(int bytesPerSecond, int blockAlign, Func<TimeSpan> clock)
    {
        if (bytesPerSecond <= 0 || blockAlign <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytesPerSecond), "The audio format must have a positive byte rate and block size.");
        }

        _bytesPerSecond = bytesPerSecond;
        _blockAlign = blockAlign;
        _clock = clock;
        _start = clock();
    }

    public bool IsPaused { get; private set; }

    public long WrittenBytes { get; private set; }

    public TimeSpan ActiveElapsed => (IsPaused ? _pausedAt : _clock()) - _start - _pausedTotal;

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

        _pausedTotal += _clock() - _pausedAt;
        IsPaused = false;
    }

    // Returns how many bytes of silence to write before the incoming block (0 when the block is dropped or on time),
    // and whether the block itself should be written. Call RecordWritten with every byte count actually written.
    public (long SilenceBytes, bool WriteData) PlanBlock(int incomingBytes)
    {
        if (IsPaused || incomingBytes <= 0)
        {
            return (0, false);
        }

        var expectedAtBlockStart = ExpectedBytes(ActiveElapsed) - incomingBytes;
        var gap = expectedAtBlockStart - WrittenBytes;
        var toleranceBytes = ExpectedBytes(GapTolerance);
        return (gap >= toleranceBytes ? gap : 0, true);
    }

    public void RecordWritten(long bytes)
    {
        WrittenBytes += bytes;
    }

    public long SilenceBytesToFinish()
    {
        return Math.Max(0, ExpectedBytes(ActiveElapsed) - WrittenBytes);
    }

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
