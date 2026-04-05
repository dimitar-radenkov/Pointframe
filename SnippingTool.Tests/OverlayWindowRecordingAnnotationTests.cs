using System.Windows;
using SnippingTool.Models;
using Xunit;

namespace SnippingTool.Tests;

public sealed class OverlayWindowRecordingAnnotationTests
{
    [Fact]
    public void CalculateRecordingCaptureBounds_ScalesRecordingBlurRegionByDpi()
    {
        var windowBounds = new Rect(100, 50, 400, 300);
        var blurRegion = new BlurShapeParameters(10, 20, 30, 40);

        var result = OverlayWindow.CalculateRecordingCaptureBounds(windowBounds, blurRegion, 1.5, 2.0);

        Assert.Equal(165, result.X);
        Assert.Equal(140, result.Y);
        Assert.Equal(45, result.Width);
        Assert.Equal(80, result.Height);
    }

    [Fact]
    public void CalculateRecordingCaptureBounds_ClampsToMinimumSize()
    {
        var windowBounds = new Rect(0, 0, 10, 10);
        var blurRegion = new BlurShapeParameters(0.2, 0.2, 0.1, 0.1);

        var result = OverlayWindow.CalculateRecordingCaptureBounds(windowBounds, blurRegion, 1.0, 1.0);

        Assert.Equal(1, result.Width);
        Assert.Equal(1, result.Height);
    }

    [Fact]
    public void CalculateRecordingCaptureBounds_FromSessionGeometry_UsesSharedMapping()
    {
        var geometry = new RecordingSessionGeometry(
            new Int32Rect(1920, 0, 2880, 1620),
            new Int32Rect(2070, 100, 1200, 800),
            new Int32Rect(1920, 0, 2880, 1560),
            new Rect(0, 0, 1920, 810),
            new Rect(0, 0, 1920, 780),
            new Rect(100, 50, 800, 400),
            "DISPLAY2",
            1.5,
            2.0);
        var blurRegion = new BlurShapeParameters(10, 20, 30, 40);

        var result = OverlayWindow.CalculateRecordingCaptureBounds(geometry, blurRegion);

        Assert.Equal(2085, result.X);
        Assert.Equal(140, result.Y);
        Assert.Equal(45, result.Width);
        Assert.Equal(80, result.Height);
    }

    [Fact]
    public void CreateRecordingSessionGeometry_BuildsSharedBoundsFromOverlayAndSelection()
    {
        var result = OverlayWindow.CreateRecordingSessionGeometry(
            new Rect(300, 200, 800, 400),
            new Int32Rect(150, 600, 1200, 800),
            "DISPLAY2",
            new Int32Rect(0, 0, 1920, 1080),
            new Int32Rect(0, 0, 1920, 1040));

        Assert.Equal(0, result.HostBoundsPixels.X);
        Assert.Equal(0, result.HostBoundsPixels.Y);
        Assert.Equal(1920, result.HostBoundsPixels.Width);
        Assert.Equal(1080, result.HostBoundsPixels.Height);
        Assert.Equal(150, result.CaptureBoundsPixels.X);
        Assert.Equal(600, result.CaptureBoundsPixels.Y);
        Assert.Equal(1200, result.CaptureBoundsPixels.Width);
        Assert.Equal(800, result.CaptureBoundsPixels.Height);
        Assert.Equal(new Int32Rect(0, 0, 1920, 1040), result.WorkAreaBoundsPixels);
        Assert.Equal(new Rect(0, 0, 1280, 540), result.HostBoundsDips);
        Assert.Equal(new Rect(0, 0, 1280, 520), result.WorkAreaBoundsDips);
        Assert.Equal(new Rect(100, 300, 800, 400), result.CaptureRectDips);
        Assert.Equal("DISPLAY2", result.MonitorName);
    }
}