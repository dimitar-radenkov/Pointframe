using System.Windows;
using Xunit;

namespace Pointframe.Tests.Models;

public sealed class CameraBubblePlacementTests
{
    private static RecordingSessionGeometry CreateGeometry(
        Int32Rect hostPixels,
        Int32Rect capturePixels,
        double scale = 1d)
    {
        return OverlayWindow.CreateRecordingSessionGeometry(
            new Rect(0, 0, capturePixels.Width / scale, capturePixels.Height / scale),
            capturePixels,
            "DISPLAY1",
            hostPixels,
            hostPixels);
    }

    private static RecordingSessionGeometry FullScreen(int width = 1920, int height = 1080, double scale = 1d, int originX = 0, int originY = 0)
    {
        var bounds = new Int32Rect(originX, originY, width, height);
        return CreateGeometry(bounds, bounds, scale);
    }

    [Theory]
    [InlineData(CameraBubbleCorner.TopLeft, 16, 16)]
    [InlineData(CameraBubbleCorner.TopRight, 1776, 16)]
    [InlineData(CameraBubbleCorner.BottomLeft, 16, 936)]
    [InlineData(CameraBubbleCorner.BottomRight, 1776, 936)]
    public void Calculate_SmallBubble_SitsAMarginInsideTheChosenCorner(CameraBubbleCorner corner, double x, double y)
    {
        var rect = CameraBubblePlacement.Calculate(FullScreen(), CameraBubbleSize.Small, corner, null);

        Assert.NotNull(rect);
        Assert.Equal(x, rect.Value.X, 3);
        Assert.Equal(y, rect.Value.Y, 3);
        Assert.Equal(128d, rect.Value.Width, 3);
        Assert.Equal(128d, rect.Value.Height, 3);
    }

    [Fact]
    public void Calculate_LargeBubble_UsesTheLargeDiameter()
    {
        var rect = CameraBubblePlacement.Calculate(FullScreen(), CameraBubbleSize.Large, CameraBubbleCorner.BottomRight, null);

        Assert.NotNull(rect);
        Assert.Equal(192d, rect.Value.Width, 3);
        Assert.Equal(1920d - 16d - 192d, rect.Value.X, 3);
        Assert.Equal(1080d - 16d - 192d, rect.Value.Y, 3);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void Calculate_KeepsTheDipDiameterAndAMarginAtEveryScale(double scale)
    {
        var geometry = FullScreen(1920, 1080, scale);

        var rect = CameraBubblePlacement.Calculate(geometry, CameraBubbleSize.Small, CameraBubbleCorner.BottomRight, null);

        Assert.NotNull(rect);
        Assert.Equal(128d, rect.Value.Width, 0);
        Assert.Equal(geometry.HostBoundsDips.Width - 16d - 128d, rect.Value.X, 0);
        Assert.Equal(geometry.HostBoundsDips.Height - 16d - 128d, rect.Value.Y, 0);
    }

    [Fact]
    public void Calculate_AtScale1_5_MapsToWholePhysicalPixels()
    {
        var geometry = FullScreen(1920, 1080, 1.5);
        var rect = CameraBubblePlacement.Calculate(geometry, CameraBubbleSize.Small, CameraBubbleCorner.BottomRight, null)!.Value;

        var pixels = geometry.MapHostDipRectToScreenPixels(rect);

        Assert.Equal(192, pixels.Width);
        Assert.Equal(192, pixels.Height);
        Assert.Equal(1920 - 24 - 192, pixels.X);
        Assert.Equal(1080 - 24 - 192, pixels.Y);
    }

    [Fact]
    public void Calculate_WithANegativeMonitorOrigin_StaysInsideTheCapturedPixels()
    {
        var geometry = FullScreen(1920, 1080, 1d, originX: -1920, originY: -200);

        var rect = CameraBubblePlacement.Calculate(geometry, CameraBubbleSize.Small, CameraBubbleCorner.TopLeft, null)!.Value;
        var pixels = geometry.MapHostDipRectToScreenPixels(rect);

        Assert.Equal(-1920 + 16, pixels.X);
        Assert.Equal(-200 + 16, pixels.Y);
        Assert.Equal(128, pixels.Width);
    }

    [Fact]
    public void Calculate_WithASubregionOnASecondMonitor_IsRelativeToTheCaptureNotTheMonitor()
    {
        var host = new Int32Rect(-1920, 0, 1920, 1080);
        var capture = new Int32Rect(-1500, 200, 800, 600);
        var geometry = CreateGeometry(host, capture);

        var rect = CameraBubblePlacement.Calculate(geometry, CameraBubbleSize.Small, CameraBubbleCorner.BottomRight, null)!.Value;
        var pixels = geometry.MapHostDipRectToScreenPixels(rect);

        Assert.Equal(-1500 + 800 - 16 - 128, pixels.X);
        Assert.Equal(200 + 600 - 16 - 128, pixels.Y);
    }

    [Fact]
    public void Calculate_WithAnOddCaptureSize_StaysInsideTheEvenTruncatedRecording()
    {
        var host = new Int32Rect(0, 0, 1920, 1080);
        var capture = new Int32Rect(100, 100, 1001, 801);
        var geometry = CreateGeometry(host, capture);

        var rect = CameraBubblePlacement.Calculate(geometry, CameraBubbleSize.Small, CameraBubbleCorner.BottomRight, null)!.Value;
        var pixels = geometry.MapHostDipRectToScreenPixels(rect);

        Assert.Equal(100 + 1000 - 16 - 128, pixels.X);
        Assert.Equal(100 + 800 - 16 - 128, pixels.Y);
        Assert.True(pixels.X + pixels.Width <= 100 + 1000);
        Assert.True(pixels.Y + pixels.Height <= 100 + 800);
    }

    [Fact]
    public void Calculate_CapsTheDiameterAtAThirdOfTheShorterSide()
    {
        var host = new Int32Rect(0, 0, 1920, 1080);
        var capture = new Int32Rect(0, 0, 600, 300);
        var geometry = CreateGeometry(host, capture);

        var rect = CameraBubblePlacement.Calculate(geometry, CameraBubbleSize.Large, CameraBubbleCorner.BottomRight, null)!.Value;

        Assert.Equal(100d, rect.Width, 3);
    }

    [Theory]
    [InlineData(150, 150)]
    [InlineData(191, 600)]
    [InlineData(600, 100)]
    public void Calculate_OmitsTheBubbleWhenTheRegionIsTooSmall(int width, int height)
    {
        var host = new Int32Rect(0, 0, 1920, 1080);
        var geometry = CreateGeometry(host, new Int32Rect(50, 50, width, height));

        Assert.Null(CameraBubblePlacement.Calculate(geometry, CameraBubbleSize.Small, CameraBubbleCorner.BottomRight, null));
    }

    [Fact]
    public void Calculate_AtTheMinimumRegionSize_StillFits()
    {
        var host = new Int32Rect(0, 0, 1920, 1080);
        var geometry = CreateGeometry(host, new Int32Rect(50, 50, 192, 192));

        var rect = CameraBubblePlacement.Calculate(geometry, CameraBubbleSize.Small, CameraBubbleCorner.BottomRight, null);

        Assert.NotNull(rect);
        Assert.Equal(64d, rect.Value.Width, 3);
    }

    [Fact]
    public void Calculate_WithAnEmptyGeometry_ReturnsNull()
    {
        Assert.Null(CameraBubblePlacement.Calculate(RecordingSessionGeometry.Empty, CameraBubbleSize.Small, CameraBubbleCorner.BottomRight, null));
    }

    [Fact]
    public void Calculate_WithTheHudInTheChosenBottomCorner_SlidesUpAlongTheEdgeAboveTheHud()
    {
        var geometry = FullScreen();
        var hud = new Rect(1500, 1000, 400, 50);

        var rect = CameraBubblePlacement.Calculate(geometry, CameraBubbleSize.Small, CameraBubbleCorner.BottomRight, hud)!.Value;

        Assert.Equal(1776d, rect.X, 3);
        Assert.Equal(1000d - 8d - 128d, rect.Y, 3);
        Assert.False(Overlaps(rect, hud));
    }

    [Fact]
    public void Calculate_WithTheHudInTheChosenTopCorner_SlidesDownBelowTheHud()
    {
        var geometry = FullScreen();
        var hud = new Rect(0, 0, 500, 60);

        var rect = CameraBubblePlacement.Calculate(geometry, CameraBubbleSize.Small, CameraBubbleCorner.TopLeft, hud)!.Value;

        Assert.Equal(16d, rect.X, 3);
        Assert.Equal(60d + 8d, rect.Y, 3);
        Assert.False(Overlaps(rect, hud));
    }

    [Fact]
    public void Calculate_WithTheHudInAnotherCorner_DoesNotMoveTheBubble()
    {
        var geometry = FullScreen();
        var hud = new Rect(0, 1000, 400, 50);

        var rect = CameraBubblePlacement.Calculate(geometry, CameraBubbleSize.Small, CameraBubbleCorner.BottomRight, hud)!.Value;

        Assert.Equal(1776d, rect.X, 3);
        Assert.Equal(936d, rect.Y, 3);
    }

    [Fact]
    public void Calculate_WithTheHudOutsideTheCapture_DoesNotMoveTheBubble()
    {
        var host = new Int32Rect(0, 0, 1920, 1080);
        var geometry = CreateGeometry(host, new Int32Rect(100, 100, 800, 600));
        var hud = new Rect(300, 720, 400, 50);

        var rect = CameraBubblePlacement.Calculate(geometry, CameraBubbleSize.Small, CameraBubbleCorner.BottomRight, hud)!.Value;

        Assert.Equal(100d + 800d - 16d - 128d, rect.X, 3);
        Assert.Equal(100d + 600d - 16d - 128d, rect.Y, 3);
    }

    [Fact]
    public void Calculate_WhenTheVerticalSlideDoesNotFit_SlidesAlongTheBottomEdgeAwayFromTheHud()
    {
        var host = new Int32Rect(0, 0, 1920, 1080);
        var geometry = CreateGeometry(host, new Int32Rect(0, 0, 600, 300));
        // The HUD covers the right side of the region over the whole height, so only a horizontal slide can clear it.
        var hud = new Rect(450, 0, 150, 300);

        var rect = CameraBubblePlacement.Calculate(geometry, CameraBubbleSize.Small, CameraBubbleCorner.BottomRight, hud)!.Value;

        Assert.False(Overlaps(rect, hud));
        Assert.True(rect.Right <= 600d - 16d + 0.001);
        Assert.True(rect.X >= 16d - 0.001);
    }

    [Fact]
    public void Calculate_WhenNoSlideFits_OmitsTheBubble()
    {
        var host = new Int32Rect(0, 0, 1920, 1080);
        var geometry = CreateGeometry(host, new Int32Rect(0, 0, 400, 300));
        var hud = new Rect(0, 0, 400, 300);

        Assert.Null(CameraBubblePlacement.Calculate(geometry, CameraBubbleSize.Small, CameraBubbleCorner.BottomRight, hud));
    }

    [Fact]
    public void Calculate_WithMixedDpiGeometry_MapsThroughTheGeometry()
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
            1.5);

        var rect = CameraBubblePlacement.Calculate(geometry, CameraBubbleSize.Small, CameraBubbleCorner.TopLeft, null)!.Value;
        var pixels = geometry.MapHostDipRectToScreenPixels(rect);

        Assert.Equal(2070 + 24, pixels.X);
        Assert.Equal(100 + 24, pixels.Y);
        Assert.Equal(192, pixels.Width);
    }

    private static bool Overlaps(Rect a, Rect b)
    {
        return a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;
    }
}
