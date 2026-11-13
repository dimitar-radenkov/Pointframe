using Pointframe.Mcp.Automation;
using Xunit;

namespace Pointframe.Tests.Mcp;

public sealed class DesktopClickGestureTests
{
    private static readonly uint LeftDown = WindowsDesktopNativeMethods.MouseEventLeftDown;
    private static readonly uint LeftUp = WindowsDesktopNativeMethods.MouseEventLeftUp;
    private static readonly uint RightDown = WindowsDesktopNativeMethods.MouseEventRightDown;
    private static readonly uint RightUp = WindowsDesktopNativeMethods.MouseEventRightUp;

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public void UnguardedGestureSendsMoveThenPressAndReleasePerClick(bool right, int count)
    {
        var sent = new List<uint>();

        var outcome = Run(right, count, () => true, flags => { sent.Add(flags); return true; });

        Assert.Equal(DesktopClickSendOutcome.Sent, outcome);
        var down = right ? RightDown : LeftDown;
        var up = right ? RightUp : LeftUp;
        var expected = new List<uint> { 0u };
        for (var index = 0; index < count; index++)
        {
            expected.Add(down);
            expected.Add(up);
        }

        Assert.Equal(expected, sent);
    }

    [Fact]
    public void GuardFailingBeforeTheMoveSendsNothing()
    {
        var sent = new List<uint>();

        var outcome = Run(false, 1, () => false, flags => { sent.Add(flags); return true; });

        Assert.Equal(DesktopClickSendOutcome.Aborted, outcome);
        Assert.Empty(sent);
    }

    [Fact]
    public void GuardFailingBeforeThePressSendsNoPressOrRelease()
    {
        var sent = new List<uint>();
        var calls = 0;

        var outcome = Run(false, 1, () => ++calls < 2, flags => { sent.Add(flags); return true; });

        Assert.Equal(DesktopClickSendOutcome.Aborted, outcome);
        Assert.Equal([0u], sent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GuardFailingWithTheButtonHeldStillReleasesAndReportsInterference(bool right)
    {
        var sent = new List<uint>();
        var calls = 0;

        var outcome = Run(right, 1, () => ++calls < 3, flags => { sent.Add(flags); return true; });

        Assert.Equal(DesktopClickSendOutcome.Interfered, outcome);
        Assert.Equal([0u, right ? RightDown : LeftDown, right ? RightUp : LeftUp], sent);
    }

    [Fact]
    public void GuardFailingBeforeTheSecondReleaseOfADoubleClickStillReleases()
    {
        var sent = new List<uint>();
        var calls = 0;

        // Guard calls: move, down, up, down, up. Fail before the second release.
        var outcome = Run(false, 2, () => ++calls < 5, flags => { sent.Add(flags); return true; });

        Assert.Equal(DesktopClickSendOutcome.Interfered, outcome);
        Assert.Equal([0u, LeftDown, LeftUp, LeftDown, LeftUp], sent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GuardThrowingWithTheButtonHeldStillAttemptsTheRelease(bool right)
    {
        var sent = new List<uint>();
        var calls = 0;

        Assert.Throws<InvalidOperationException>(() => Run(
            right,
            1,
            () => ++calls < 3 ? true : throw new InvalidOperationException("guard"),
            flags => { sent.Add(flags); return true; }));

        Assert.Equal([0u, right ? RightDown : LeftDown, right ? RightUp : LeftUp], sent);
    }

    [Fact]
    public void GuardThrowingDuringTheSecondPressOfADoubleClickAttemptsItsRelease()
    {
        var sent = new List<uint>();
        var calls = 0;

        Assert.Throws<InvalidOperationException>(() => Run(
            false,
            2,
            () => ++calls < 5 ? true : throw new InvalidOperationException("guard"),
            flags => { sent.Add(flags); return true; }));

        Assert.Equal([0u, LeftDown, LeftUp, LeftDown, LeftUp], sent);
    }

    [Fact]
    public void ExceptionAfterThePressSendsTheRelease()
    {
        var sent = new List<uint>();
        var sleeps = 0;
        var steps = WindowsDesktopInputNativeAdapter.BuildClickSteps(false, 1);

        Assert.Throws<InvalidOperationException>(() => DesktopClickGesture.Run(
            steps,
            LeftDown,
            LeftUp,
            () => true,
            flags => { sent.Add(flags); return true; },
            _ =>
            {
                if (++sleeps == 2)
                {
                    throw new InvalidOperationException("sleep");
                }
            }));

        Assert.Equal([0u, LeftDown, LeftUp], sent);
    }

    [Fact]
    public void NativeSendThrowingOnTheReleaseTriesTheReleaseAgain()
    {
        var sent = new List<uint>();
        var releases = 0;

        Assert.Throws<InvalidOperationException>(() => Run(false, 1, () => true, flags =>
        {
            sent.Add(flags);
            if (flags == LeftUp && ++releases == 1)
            {
                throw new InvalidOperationException("send");
            }

            return true;
        }));

        Assert.Equal(2, sent.Count(flags => flags == LeftUp));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedReleaseIsADispatchFailureAndIsRetried(bool right)
    {
        var sent = new List<uint>();
        var up = right ? RightUp : LeftUp;

        var outcome = Run(right, 1, () => true, flags => { sent.Add(flags); return flags != up; });

        Assert.Equal(DesktopClickSendOutcome.Failed, outcome);
        Assert.Equal(2, sent.Count(flags => flags == up));
    }

    [Fact]
    public void FailedPressIsADispatchFailureWithNoReleaseAttempt()
    {
        var sent = new List<uint>();

        var outcome = Run(false, 1, () => true, flags => { sent.Add(flags); return flags != LeftDown; });

        Assert.Equal(DesktopClickSendOutcome.Failed, outcome);
        Assert.DoesNotContain(LeftUp, sent);
    }

    private static DesktopClickSendOutcome Run(bool right, int count, Func<bool> guard, Func<uint, bool> send) =>
        DesktopClickGesture.Run(
            WindowsDesktopInputNativeAdapter.BuildClickSteps(right, count),
            right ? RightDown : LeftDown,
            right ? RightUp : LeftUp,
            guard,
            send,
            _ => { });
}
