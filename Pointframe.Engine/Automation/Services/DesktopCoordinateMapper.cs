using Pointframe.Engine.Automation.Models;

namespace Pointframe.Engine.Automation.Services;

public sealed class DesktopCoordinateMapper(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public PixelBounds ToDesktopPixels(
        DesktopImageReference image,
        int imageX,
        int imageY,
        int expectedTopologyGeneration,
        int actualTopologyGeneration = 0)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (actualTopologyGeneration != 0 && actualTopologyGeneration != expectedTopologyGeneration)
        {
            throw new DesktopOperationException("StaleObservation", "The monitor topology changed since the observation.");
        }

        if (_timeProvider.GetUtcNow() - image.CapturedUtc > TimeSpan.FromSeconds(DesktopTestingLimits.ObservationTtlSeconds))
        {
            throw new DesktopOperationException("StaleObservation", "The observation has expired.");
        }

        if (imageX < 0 || imageX >= image.Width || imageY < 0 || imageY >= image.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(imageX), "The image coordinate is outside the captured image.");
        }

        return new PixelBounds(image.DesktopBoundsPixels.X + imageX, image.DesktopBoundsPixels.Y + imageY, 1, 1);
    }

    public PixelBounds ToDesktopPixels(
        DesktopCoordinateTransform transform,
        int imageX,
        int imageY,
        int actualTopologyGeneration = 0)
    {
        ArgumentNullException.ThrowIfNull(transform);
        if (actualTopologyGeneration != 0 && actualTopologyGeneration != transform.TopologyGeneration)
        {
            throw new DesktopOperationException("StaleObservation", "The monitor topology changed since the observation.");
        }

        if (_timeProvider.GetUtcNow() - transform.CapturedUtc > TimeSpan.FromSeconds(DesktopTestingLimits.ObservationTtlSeconds))
        {
            throw new DesktopOperationException("StaleObservation", "The observation has expired.");
        }

        if (imageX < 0 || imageX >= transform.ImageWidth || imageY < 0 || imageY >= transform.ImageHeight)
        {
            throw new ArgumentOutOfRangeException(nameof(imageX), "The image coordinate is outside the captured image.");
        }

        var desktopX = transform.DesktopBoundsPixels.X
            + (int)Math.Floor(imageX * (double)transform.DesktopBoundsPixels.Width / transform.ImageWidth);
        var desktopY = transform.DesktopBoundsPixels.Y
            + (int)Math.Floor(imageY * (double)transform.DesktopBoundsPixels.Height / transform.ImageHeight);
        return new PixelBounds(desktopX, desktopY, 1, 1);
    }
}
