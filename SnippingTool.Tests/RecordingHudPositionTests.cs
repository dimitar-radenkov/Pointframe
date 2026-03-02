using System.Windows;
using Xunit;

namespace SnippingTool.Tests;

public sealed class RecordingHudPositionTests
{
    // Work area used by all tests: 0,0 → 1920×1040
    private static readonly Rect WorkArea = new(0, 0, 1920, 1040);
    // HUD dimensions
    private const double HudW = 140;
    private const double HudH = 44;

    [Fact]
    public void ComputePosition_NormalRegion_CenteredBelowWithGap()
    {
        // Arrange
        var region = new Rect(600, 200, 400, 300);

        // Act
        var (left, top) = RecordingHudWindow.ComputePosition(region, HudW, HudH, WorkArea);

        // Assert
        Assert.Equal(730, left);
        Assert.Equal(508, top);
    }

    [Fact]
    public void ComputePosition_RegionNearRightEdge_ClampsLeft()
    {
        // Arrange
        var region = new Rect(1850, 200, 400, 300);

        // Act
        var (left, _) = RecordingHudWindow.ComputePosition(region, HudW, HudH, WorkArea);

        // Assert
        Assert.Equal(1780, left);
    }

    [Fact]
    public void ComputePosition_RegionNearLeftEdge_ClampsLeft()
    {
        // Arrange
        var region = new Rect(-300, 200, 200, 300);

        // Act
        var (left, _) = RecordingHudWindow.ComputePosition(region, HudW, HudH, WorkArea);

        // Assert
        Assert.Equal(0, left);
    }

    [Fact]
    public void ComputePosition_RegionNearBottom_ClampsTop()
    {
        // Arrange
        var region = new Rect(600, 1020, 400, 60);

        // Act
        var (_, top) = RecordingHudWindow.ComputePosition(region, HudW, HudH, WorkArea);

        // Assert
        Assert.Equal(WorkArea.Bottom - HudH, top);
    }
}
