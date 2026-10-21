using Pointframe.Engine;
using Pointframe.Mcp.Automation;
using Xunit;

namespace Pointframe.Tests.Mcp;

public class DesktopNativeInputCorrectnessTests
{
    [Fact]
    public void NormalizeAbsoluteMapsPrimaryMonitorOriginToZero()
    {
        var virtualScreen = new PixelBounds(0, 0, 1920, 1080);

        var (x, y) = WindowsDesktopInputNativeAdapter.NormalizeAbsolute(0, 0, virtualScreen);

        Assert.Equal(0, x);
        Assert.Equal(0, y);
    }

    [Fact]
    public void NormalizeAbsoluteMapsFarCornerToFullRange()
    {
        var virtualScreen = new PixelBounds(0, 0, 1920, 1080);

        var (x, y) = WindowsDesktopInputNativeAdapter.NormalizeAbsolute(1919, 1079, virtualScreen);

        Assert.Equal(65535, x);
        Assert.Equal(65535, y);
    }

    [Fact]
    public void NormalizeAbsoluteHandlesMonitorLeftOfPrimary()
    {
        // A secondary monitor placed to the left of the primary gives the virtual screen a negative
        // origin. Normalizing against that origin is what makes a click on it land where it was aimed.
        var virtualScreen = new PixelBounds(-1920, 0, 3840, 1080);

        var (left, _) = WindowsDesktopInputNativeAdapter.NormalizeAbsolute(-1920, 0, virtualScreen);
        var (middle, _) = WindowsDesktopInputNativeAdapter.NormalizeAbsolute(0, 0, virtualScreen);

        Assert.Equal(0, left);
        Assert.InRange(middle, 32750, 32790);
    }

    [Fact]
    public void BuildDragPathExpandsTwoPointsIntoEnoughMoves()
    {
        IReadOnlyList<PixelBounds> points =
        [
            new PixelBounds(100, 100, 1, 1),
            new PixelBounds(200, 200, 1, 1),
        ];

        var path = WindowsDesktopInputNativeAdapter.BuildDragPath(points);

        Assert.True(
            path.Count - 1 >= WindowsDesktopInputNativeAdapter.MinDragMoveSteps,
            $"Expected at least {WindowsDesktopInputNativeAdapter.MinDragMoveSteps} move steps but got {path.Count - 1}.");
    }

    [Fact]
    public void BuildDragPathKeepsTheRequestedStartAndEnd()
    {
        IReadOnlyList<PixelBounds> points =
        [
            new PixelBounds(10, 20, 1, 1),
            new PixelBounds(310, 220, 1, 1),
        ];

        var path = WindowsDesktopInputNativeAdapter.BuildDragPath(points);

        Assert.Equal(10, path[0].X);
        Assert.Equal(20, path[0].Y);
        Assert.Equal(310, path[^1].X);
        Assert.Equal(220, path[^1].Y);
    }

    [Fact]
    public void BuildDragPathIsMonotonicAlongTheRequestedDirection()
    {
        IReadOnlyList<PixelBounds> points =
        [
            new PixelBounds(0, 0, 1, 1),
            new PixelBounds(100, 50, 1, 1),
        ];

        var path = WindowsDesktopInputNativeAdapter.BuildDragPath(points);

        for (var index = 1; index < path.Count; index++)
        {
            Assert.True(path[index].X >= path[index - 1].X, "The drag path moved backwards on X.");
            Assert.True(path[index].Y >= path[index - 1].Y, "The drag path moved backwards on Y.");
        }
    }

    [Fact]
    public void BuildDragPathLeavesACallerSuppliedPathAlone()
    {
        IReadOnlyList<PixelBounds> points = Enumerable
            .Range(0, WindowsDesktopInputNativeAdapter.MinDragMoveSteps + 4)
            .Select(step => new PixelBounds(step * 10, step * 10, 1, 1))
            .ToArray();

        var path = WindowsDesktopInputNativeAdapter.BuildDragPath(points);

        Assert.Same(points, path);
    }

    [Fact]
    public void ClickStepsMoveBeforePressing()
    {
        var steps = WindowsDesktopInputNativeAdapter.BuildClickSteps(rightButton: false, count: 1);

        Assert.Equal(0u, steps[0].Flags);
        Assert.True(steps[0].DelayAfterMilliseconds > 0, "The pointer must be given time to settle before the press.");
    }

    [Fact]
    public void ClickStepsHoldTheButtonBeforeReleasing()
    {
        // A press and release batched with no delay is discarded as noise by many controls, which is
        // how a click could report success and do nothing at all.
        var steps = WindowsDesktopInputNativeAdapter.BuildClickSteps(rightButton: false, count: 1);

        var pressIndex = steps.ToList().FindIndex(step => step.Flags == 0x0002);
        Assert.True(pressIndex >= 0, "No left button press was emitted.");
        Assert.True(
            steps[pressIndex].DelayAfterMilliseconds >= 20,
            $"The button was held for only {steps[pressIndex].DelayAfterMilliseconds} ms.");
        Assert.Equal(0x0004u, steps[pressIndex + 1].Flags);
    }

    [Fact]
    public void ClickStepsEmitOnePressAndReleasePerRequestedClick()
    {
        var steps = WindowsDesktopInputNativeAdapter.BuildClickSteps(rightButton: false, count: 2);

        Assert.Equal(2, steps.Count(step => step.Flags == 0x0002));
        Assert.Equal(2, steps.Count(step => step.Flags == 0x0004));
    }

    [Fact]
    public void ClickStepsKeepADoubleClickInsideTheSystemInterval()
    {
        var steps = WindowsDesktopInputNativeAdapter.BuildClickSteps(rightButton: false, count: 2);

        // Everything between the first release and the second press has to fit inside the default
        // 500 ms double-click time or the two clicks arrive as unrelated single clicks.
        var firstRelease = steps.ToList().FindIndex(step => step.Flags == 0x0004);
        Assert.True(steps[firstRelease].DelayAfterMilliseconds < 500);
        Assert.True(steps[firstRelease].DelayAfterMilliseconds > 0);
    }

    [Fact]
    public void ClickStepsUseRightButtonFlagsWhenAsked()
    {
        var steps = WindowsDesktopInputNativeAdapter.BuildClickSteps(rightButton: true, count: 1);

        Assert.Contains(steps, step => step.Flags == 0x0008);
        Assert.Contains(steps, step => step.Flags == 0x0010);
        Assert.DoesNotContain(steps, step => step.Flags == 0x0002);
    }
}
