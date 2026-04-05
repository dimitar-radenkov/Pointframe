using System.Windows;
using SnippingTool.Models;
using Xunit;

namespace SnippingTool.Tests.Models;

public sealed class RecordingSessionGeometryTests
{
    private static readonly RecordingSessionGeometry Geometry = new(
        new Int32Rect(1920, 0, 2880, 1620),
        new Int32Rect(2070, 100, 1200, 800),
        new Int32Rect(1920, 0, 2880, 1560),
        new Rect(0, 0, 1920, 810),
        new Rect(0, 0, 1920, 780),
        new Rect(100, 50, 800, 400),
        "DISPLAY2",
        1.5,
        2.0);

    [Fact]
    public void MapHostDipRectToScreenPixels_ScalesFromHostSpace()
    {
        var result = Geometry.MapHostDipRectToScreenPixels(new Rect(110, 70, 30, 40));

        Assert.Equal(2085, result.X);
        Assert.Equal(140, result.Y);
        Assert.Equal(45, result.Width);
        Assert.Equal(80, result.Height);
    }

    [Fact]
    public void MapCaptureLocalDipRectToScreenPixels_UsesCaptureRectOffset()
    {
        var result = Geometry.MapCaptureLocalDipRectToScreenPixels(new Rect(10, 20, 30, 40));

        Assert.Equal(2085, result.X);
        Assert.Equal(140, result.Y);
        Assert.Equal(45, result.Width);
        Assert.Equal(80, result.Height);
    }

    [Fact]
    public void MapScreenPixelPointToHostDips_RestoresHostCoordinates()
    {
        var result = Geometry.MapScreenPixelPointToHostDips(new Point(2085, 140));

        Assert.Equal(110, result.X, 6);
        Assert.Equal(70, result.Y, 6);
    }

    [Fact]
    public void MapScreenPixelRectToHostDips_RoundTripsPixelBounds()
    {
        var result = Geometry.MapScreenPixelRectToHostDips(new Int32Rect(2085, 140, 45, 80));

        Assert.Equal(110, result.X, 6);
        Assert.Equal(70, result.Y, 6);
        Assert.Equal(30, result.Width, 6);
        Assert.Equal(40, result.Height, 6);
    }

    [Fact]
    public void MapHostDipRectToScreenPixels_ClampsMinimumSizeToOnePixel()
    {
        var tiny = new RecordingSessionGeometry(
            new Int32Rect(0, 0, 100, 100),
            new Int32Rect(0, 0, 100, 100),
            new Int32Rect(0, 0, 100, 100),
            new Rect(0, 0, 100, 100),
            new Rect(0, 0, 100, 100),
            new Rect(0, 0, 100, 100),
            "DISPLAY1",
            1.0,
            1.0);

        var result = tiny.MapHostDipRectToScreenPixels(new Rect(1, 1, 0.1, 0.1));

        Assert.Equal(1, result.Width);
        Assert.Equal(1, result.Height);
    }

    [Fact]
    public void GetRecordingBorderRectDips_ExpandsCaptureRectByOffset()
    {
        var result = Geometry.GetRecordingBorderRectDips(8);

        Assert.Equal(new Rect(92, 42, 816, 416), result);
    }

    [Fact]
    public void IsScreenPixelPointInsideCapture_UsesSharedCaptureMapping()
    {
        Assert.True(Geometry.IsScreenPixelPointInsideCapture(new Point(2085, 140)));
        Assert.False(Geometry.IsScreenPixelPointInsideCapture(new Point(2000, 40)));
    }

    [Fact]
    public void IsScreenPixelPointInsideHostRect_UsesHostDipCoordinates()
    {
        var hudRect = new Rect(110, 460, 200, 40);

        Assert.True(Geometry.IsScreenPixelPointInsideHostRect(new Point(2100, 940), hudRect));
        Assert.False(Geometry.IsScreenPixelPointInsideHostRect(new Point(2500, 140), hudRect));
    }
}