using Pointframe.Engine;
using Pointframe.Engine.Automation.Models;
using Pointframe.Engine.Automation.Services;
using Xunit;

namespace Pointframe.Tests.Engine;

public sealed class DesktopCoordinateMapperTests
{
    [Fact]
    public void ToDesktopPixels_PreservesNegativeDesktopOrigins()
    {
        var captured = DateTimeOffset.UtcNow;
        var image = new DesktopImageReference("image-1", 100, 80, new PixelBounds(-1920, 20, 100, 80), captured);

        var result = new DesktopCoordinateMapper().ToDesktopPixels(image, 7, 11, 4);

        Assert.Equal(new PixelBounds(-1913, 31, 1, 1), result);
    }

    [Fact]
    public void ToDesktopPixels_RejectsTopologyChanges()
    {
        var image = new DesktopImageReference("image-1", 10, 10, new PixelBounds(0, 0, 10, 10), DateTimeOffset.UtcNow);

        var exception = Assert.Throws<DesktopOperationException>(() =>
            new DesktopCoordinateMapper().ToDesktopPixels(image, 1, 1, 4, actualTopologyGeneration: 5));

        Assert.Equal("StaleObservation", exception.Code);
    }

    [Fact]
    public void ToDesktopPixels_ScalesImageCoordinatesToDesktopBounds()
    {
        var transform = new DesktopCoordinateTransform(
            "image-1",
            new PixelBounds(-100, 20, 200, 100),
            100,
            50,
            1,
            DateTimeOffset.UtcNow);

        var result = new DesktopCoordinateMapper().ToDesktopPixels(transform, 50, 25);

        Assert.Equal(new PixelBounds(0, 70, 1, 1), result);
    }
}
