using System.Windows;
using Xunit;

namespace SnippingTool.Tests;

public sealed class OverlayWindowLayoutTests
{
    [Fact]
    public void CalculateRecordingBorderRect_ExpandsSelectionByOffsetOnAllSides()
    {
        var selectionRect = new Rect(533.3333333333333, -880, 540, 474.6666666666665);

        var result = OverlayWindow.CalculateRecordingBorderRect(selectionRect, 8d);

        Assert.Equal(525.3333333333333, result.Left, 10);
        Assert.Equal(-888, result.Top, 10);
        Assert.Equal(556, result.Width, 10);
        Assert.Equal(490.6666666666665, result.Height, 10);
    }

    [Fact]
    public void CalculateRecordingBorderRect_OnSecondaryMonitorCoordinates_RemainsOutsideCaptureRegion()
    {
        var selectionRect = new Rect(539.3333333333333, -877.3333333333334, 462.66666666666674, 469.9999999999999);

        var result = OverlayWindow.CalculateRecordingBorderRect(selectionRect, 8d);

        Assert.True(result.Left < selectionRect.Left);
        Assert.True(result.Top < selectionRect.Top);
        Assert.True(result.Right > selectionRect.Right);
        Assert.True(result.Bottom > selectionRect.Bottom);
    }

    [Fact]
    public void CalculateOpenedImageDisplayRect_CentersImageInsideSingleMonitorTargetArea()
    {
        var targetArea = new Rect(1920, 0, 1920, 1400);

        var result = OverlayWindow.CalculateOpenedImageDisplayRect(1600, 900, targetArea, 140);

        Assert.True(result.Left >= targetArea.Left);
        Assert.True(result.Right <= targetArea.Right);
        Assert.True(result.Top >= targetArea.Top);
        Assert.True(result.Bottom <= targetArea.Bottom);
        Assert.Equal(1600, result.Width);
        Assert.Equal(900, result.Height);
    }

    [Fact]
    public void CalculateOpenedImageDisplayRect_ScalesDownLargeImageWithoutCrossingMonitorBounds()
    {
        var targetArea = new Rect(1920, 0, 1920, 1040);

        var result = OverlayWindow.CalculateOpenedImageDisplayRect(4000, 3000, targetArea, 140);

        Assert.True(result.Left >= targetArea.Left);
        Assert.True(result.Right <= targetArea.Right);
        Assert.True(result.Top >= targetArea.Top);
        Assert.True(result.Bottom <= targetArea.Bottom);
        Assert.True(result.Width <= (targetArea.Width - 280) + 0.01);
        Assert.True(result.Height <= (targetArea.Height - 280) + 0.01);
    }

    [Fact]
    public void CalculateWindowBounds_MapsMonitorPixelsIntoMonitorLocalDips()
    {
        var screenBounds = new System.Drawing.Rectangle(1440, -2160, 2560, 1440);

        var result = MonitorDpiHelper.CalculateWindowBounds(screenBounds, 1.5);

        Assert.Equal(960, result.Left);
        Assert.Equal(-1440, result.Top);
        Assert.Equal(1706.6666666666667, result.Width, 10);
        Assert.Equal(960, result.Height, 10);
    }
}