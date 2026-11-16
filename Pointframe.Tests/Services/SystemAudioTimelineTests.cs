using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class SystemAudioTimelineTests
{
    private const int BytesPerSecond = 192000;
    private const int BlockAlign = 8;
    private const int TenMs = BytesPerSecond / 100;

    private TimeSpan _now = TimeSpan.Zero;

    private SystemAudioTimeline Create() => new(BytesPerSecond, BlockAlign, () => _now);

    private static TimeSpan Ms(double value) => TimeSpan.FromMilliseconds(value);

    private static long Total(IReadOnlyList<SystemAudioPacketPlan> plans) => plans.Sum(plan => plan.SilenceBytesBefore + plan.Segment.Length);

    [Fact]
    public void PlanPacket_WhenPacketsAreContiguous_NeedsNoSilence()
    {
        var timeline = Create();
        _now = Ms(30);

        var first = timeline.PlanPacket(Ms(0), TenMs);
        var second = timeline.PlanPacket(Ms(10), TenMs);

        Assert.Equal(0, first.Single().SilenceBytesBefore);
        Assert.Equal(0, second.Single().SilenceBytesBefore);
        Assert.Equal(2 * TenMs, timeline.WrittenBytes);
    }

    [Fact]
    public void PlanPacket_BatchedPacketsDeliveredLate_DoNotCreateFalseSilence()
    {
        var timeline = Create();
        _now = Ms(200);

        var plans = Enumerable.Range(0, 10).SelectMany(i => timeline.PlanPacket(Ms(i * 10), TenMs)).ToList();

        Assert.All(plans, plan => Assert.Equal(0, plan.SilenceBytesBefore));
        Assert.Equal(10 * TenMs, timeline.WrittenBytes);
        Assert.Equal(10 * TenMs, Total(plans));
    }

    [Fact]
    public void PlanPacket_WhenNothingPlayedForAWhile_FillsTheRealGapOnce()
    {
        var timeline = Create();
        _now = Ms(2100);
        timeline.PlanPacket(Ms(0), TenMs);

        var plans = timeline.PlanPacket(Ms(2000), TenMs);

        Assert.Equal(2000 * BytesPerSecond / 1000 - TenMs, plans.Single().SilenceBytesBefore);
        Assert.Equal(2010 * BytesPerSecond / 1000, timeline.WrittenBytes);
    }

    [Fact]
    public void PlanPacket_WhenAudioStartsAfterTheRecordingStarted_LeadPadsSilence()
    {
        var timeline = Create();
        _now = Ms(500);

        var plans = timeline.PlanPacket(Ms(400), TenMs);

        Assert.Equal(400 * BytesPerSecond / 1000, plans.Single().SilenceBytesBefore);
    }

    [Fact]
    public void PlanPacket_TrimsTheBeginningOfAPacketCapturedBeforeTheRecordingStarted()
    {
        _now = Ms(100);
        var timeline = Create();

        var plans = timeline.PlanPacket(Ms(90), 2 * TenMs);

        var segment = plans.Single().Segment;
        Assert.Equal(TenMs, segment.Offset);
        Assert.Equal(TenMs, segment.Length);
    }

    [Fact]
    public void PlanPacket_PacketCapturedDuringPauseButDeliveredAfterResume_IsDropped()
    {
        _now = Ms(0);
        var timeline = Create();
        timeline.PlanPacket(Ms(0), TenMs);
        _now = Ms(100);
        timeline.Pause();
        _now = Ms(1100);
        timeline.Resume();
        _now = Ms(1110);

        var plans = timeline.PlanPacket(Ms(500), TenMs);

        Assert.Empty(plans);
        Assert.Equal(TenMs, timeline.WrittenBytes);
    }

    [Fact]
    public void PlanPacket_PacketStraddlingTheResumeBoundary_KeepsOnlyTheActivePart()
    {
        var timeline = Create();
        timeline.PlanPacket(Ms(0), 10 * TenMs);
        _now = Ms(100);
        timeline.Pause();
        _now = Ms(1000);
        timeline.Resume();
        _now = Ms(1020);

        var plans = timeline.PlanPacket(Ms(995), 2 * TenMs);

        var plan = plans.Single();
        Assert.Equal(5 * BytesPerSecond / 1000, plan.Segment.Offset);
        Assert.Equal(15 * BytesPerSecond / 1000, plan.Segment.Length);
        Assert.Equal(0, plan.SilenceBytesBefore);
    }

    [Fact]
    public void PlanPacket_PacketStraddlingThePauseBoundary_KeepsOnlyTheActivePart()
    {
        var timeline = Create();
        _now = Ms(100);
        timeline.Pause();

        var plans = timeline.PlanPacket(Ms(90), 2 * TenMs);

        Assert.Equal(TenMs, Assert.Single(plans).Segment.Length);
    }

    [Fact]
    public void ActiveElapsed_ExcludesPausedTime()
    {
        var timeline = Create();
        _now = Ms(1000);
        timeline.Pause();
        _now = Ms(11000);
        Assert.Equal(Ms(1000), timeline.ActiveElapsed);
        timeline.Resume();
        _now = Ms(12000);

        Assert.Equal(Ms(2000), timeline.ActiveElapsed);
    }

    [Fact]
    public void SilenceBytesToFinish_PadsToTheActiveLengthInWholeBlocks()
    {
        var timeline = Create();
        _now = TimeSpan.FromSeconds(3) + TimeSpan.FromTicks(1);
        timeline.PlanPacket(Ms(0), BytesPerSecond);

        var padding = timeline.SilenceBytesToFinish();

        Assert.Equal(2 * BytesPerSecond, padding);
        Assert.Equal(0, padding % BlockAlign);
    }

    [Fact]
    public void SilenceBytesToFinish_WhenAudioIsAlreadyLongEnough_IsZero()
    {
        var timeline = Create();
        _now = Ms(1000);
        timeline.PlanPacket(Ms(0), 2 * BytesPerSecond);

        Assert.Equal(0, timeline.SilenceBytesToFinish());
    }

    [Fact]
    public void PlanPacket_WithAnExplicitOrigin_PlacesAudioRelativeToIt()
    {
        _now = Ms(1000);
        var timeline = new SystemAudioTimeline(BytesPerSecond, BlockAlign, () => _now, Ms(500));

        var plans = timeline.PlanPacket(Ms(500), TenMs);

        Assert.Equal(0, plans.Single().SilenceBytesBefore);
        Assert.Equal(0, plans.Single().Segment.Offset);
        Assert.Equal(Ms(500), timeline.ActiveElapsed);
    }

    [Fact]
    public void PlanPacket_FlaggedZeroTimestamp_IsPlacedAfterThePreviousPacketAndNotDropped()
    {
        var timeline = Create();
        _now = Ms(520);
        timeline.PlanPacket(Ms(500), TenMs);

        var plans = timeline.PlanPacket(TimeSpan.Zero, TenMs, timestampValid: false);

        var plan = plans.Single();
        Assert.Equal(0, plan.SilenceBytesBefore);
        Assert.Equal(TenMs, plan.Segment.Length);
    }

    [Fact]
    public void PlanPacket_FlaggedForwardJump_DoesNotInsertSilence()
    {
        var timeline = Create();
        _now = Ms(100);
        timeline.PlanPacket(Ms(0), TenMs);

        var plans = timeline.PlanPacket(Ms(60000), TenMs, timestampValid: false);

        Assert.Equal(0, plans.Single().SilenceBytesBefore);
        Assert.Equal(2 * TenMs, timeline.WrittenBytes);
    }

    [Fact]
    public void PlanPacket_FlaggedPacketAfterALongQuietStretch_LandsAtItsArrivalTimeWithSilenceBefore()
    {
        var timeline = Create();
        _now = Ms(10);
        timeline.PlanPacket(Ms(0), TenMs);
        _now = Ms(5000);

        var plans = timeline.PlanPacket(Ms(5000), TenMs, timestampValid: false);

        var plan = plans.Single();
        Assert.Equal(4990 * BytesPerSecond / 1000 - TenMs, plan.SilenceBytesBefore);
        Assert.Equal(5000 * BytesPerSecond / 1000, timeline.WrittenBytes);
    }

    [Fact]
    public void PlanPacket_FlaggedPacketAfterAPacketlessPause_IsPlacedAfterResumeNotInsideThePause()
    {
        var timeline = Create();
        _now = Ms(10);
        timeline.PlanPacket(Ms(0), TenMs);
        _now = Ms(100);
        timeline.Pause();
        _now = Ms(1000);
        timeline.Resume();
        _now = Ms(1020);

        var plans = timeline.PlanPacket(Ms(0), TenMs, timestampValid: false);

        var plan = plans.Single();
        Assert.Equal(TenMs, plan.Segment.Length);
        Assert.Equal(0, plan.Segment.Offset);
        Assert.Equal(100 * BytesPerSecond / 1000, plan.SilenceBytesBefore);
        Assert.Equal(120 * BytesPerSecond / 1000, timeline.WrittenBytes);
    }

    [Fact]
    public void Constructor_RejectsAnInvalidFormat()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SystemAudioTimeline(0, BlockAlign, () => _now));
    }
}
