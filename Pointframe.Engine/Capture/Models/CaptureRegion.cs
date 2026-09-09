namespace Pointframe.Engine;

/// <summary>
/// A sub-rectangle expressed relative to a monitor's own top-left corner, as opposed to
/// <see cref="PixelBounds"/> which is always in absolute physical-pixel screen coordinates.
/// </summary>
public readonly record struct CaptureRegion(int X, int Y, int Width, int Height)
{
    /// <summary>
    /// Converts this monitor-local region into absolute physical-pixel bounds within
    /// <paramref name="monitorBoundsPixels"/>, rejecting regions that are not entirely contained by it.
    /// </summary>
    public PixelBounds ResolveWithin(PixelBounds monitorBoundsPixels)
    {
        if (Width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Width), Width, "The capture region width must be positive.");
        }

        if (Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Height), Height, "The capture region height must be positive.");
        }

        if (X < 0 || Y < 0 || X + Width > monitorBoundsPixels.Width || Y + Height > monitorBoundsPixels.Height)
        {
            throw new ArgumentOutOfRangeException(
                nameof(monitorBoundsPixels),
                this,
                $"The capture region must be within the monitor bounds ({monitorBoundsPixels.Width}x{monitorBoundsPixels.Height}).");
        }

        return new PixelBounds(monitorBoundsPixels.X + X, monitorBoundsPixels.Y + Y, Width, Height);
    }
}
