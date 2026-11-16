using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class SystemAudioTimelineTests
{
    private const int BytesPerSecond = 192000;
    private const int BlockAlign = 8;

    private TimeSpan _now = TimeSpan.Zero;

    private SystemAudioTimeline Create() => new(BytesPerSecond, BlockAlign, () => _now);

    [Fact]
    public void PlanBlock_WhenDataArrivesOnTime_NeedsNoSilence()
    {
        var timeline = Create();
        _now = TimeSpan.FromMilliseconds(10);

        var (silence, write) = timeline.PlanBlock(BytesPerSecond / 100);

        Assert.Equal(0, silence);
        Assert.True(write);
    }

    [Fact]
    public void PlanBlock_AfterQuietPeriod_FillsTheGapWithSilence()
    {
        var timeline = Create();
        _now = TimeSpan.FromSeconds(2);
        var blockBytes = BytesPerSecond / 10;

        var (silence, write) = timeline.PlanBlock(blockBytes);

        Assert.True(write);
        Assert.Equal(2 * BytesPerSecond - blockBytes, silence);
    }

    [Fact]
    public void PlanBlock_GapBelowTolerance_IsNotFilled()
    {
        var timeline = Create();
        _now = TimeSpan.FromMilliseconds(150);
        var blockBytes = BytesPerSecond / 10;

        var (silence, _) = timeline.PlanBlock(blockBytes);

        Assert.Equal(0, silence);
    }

    [Fact]
    public void Pause_DropsDataAndExcludesPausedTimeFromTheTimeline()
    {
        var timeline = Create();
        _now = TimeSpan.FromSeconds(1);
        timeline.RecordWritten(BytesPerSecond);
        timeline.Pause();
        _now = TimeSpan.FromSeconds(11);

        var (silence, write) = timeline.PlanBlock(1000);
        Assert.False(write);
        Assert.Equal(0, silence);
        Assert.Equal(TimeSpan.FromSeconds(1), timeline.ActiveElapsed);

        timeline.Resume();
        _now = TimeSpan.FromSeconds(12);

        Assert.Equal(TimeSpan.FromSeconds(2), timeline.ActiveElapsed);
        Assert.Equal(BytesPerSecond, timeline.SilenceBytesToFinish());
    }

    [Fact]
    public void SilenceBytesToFinish_PadsToTheActiveLengthInWholeBlocks()
    {
        var timeline = Create();
        _now = TimeSpan.FromSeconds(3) + TimeSpan.FromTicks(1);
        timeline.RecordWritten(BytesPerSecond);

        var padding = timeline.SilenceBytesToFinish();

        Assert.Equal(2 * BytesPerSecond, padding);
        Assert.Equal(0, padding % BlockAlign);
    }

    [Fact]
    public void SilenceBytesToFinish_WhenAudioIsAlreadyLongEnough_IsZero()
    {
        var timeline = Create();
        _now = TimeSpan.FromSeconds(1);
        timeline.RecordWritten(2 * BytesPerSecond);

        Assert.Equal(0, timeline.SilenceBytesToFinish());
    }

    [Fact]
    public void Constructor_RejectsAnInvalidFormat()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SystemAudioTimeline(0, BlockAlign, () => _now));
    }
}
