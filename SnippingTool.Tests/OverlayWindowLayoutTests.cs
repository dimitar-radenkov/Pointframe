using System.Windows;
using Xunit;

namespace SnippingTool.Tests;

public sealed class OverlayWindowLayoutTests
{
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
}