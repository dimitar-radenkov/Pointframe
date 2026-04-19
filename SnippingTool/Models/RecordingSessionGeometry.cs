using System.Windows;

namespace Pointframe.Models;

public sealed record RecordingSessionGeometry(
    Int32Rect HostBoundsPixels,
    Int32Rect CaptureBoundsPixels,
    Int32Rect WorkAreaBoundsPixels,
    Rect HostBoundsDips,
    Rect WorkAreaBoundsDips,
    Rect CaptureRectDips,
    string MonitorName,
    double MonitorScaleX,
    double MonitorScaleY)
{
    public static RecordingSessionGeometry Empty { get; } = new(
        new Int32Rect(),
        new Int32Rect(),
        new Int32Rect(),
        Rect.Empty,
        Rect.Empty,
        Rect.Empty,
        string.Empty,
        1d,
        1d);

    public bool IsEmpty => HostBoundsPixels.Width <= 0 || HostBoundsPixels.Height <= 0;

    public Point MapHostDipPointToScreenPixels(Point hostPoint)
    {
        EnsureValidScale();

        return new Point(
            HostBoundsPixels.X + ((hostPoint.X - HostBoundsDips.X) * MonitorScaleX),
            HostBoundsPixels.Y + ((hostPoint.Y - HostBoundsDips.Y) * MonitorScaleY));
    }

    public Point MapScreenPixelPointToHostDips(Point screenPoint)
    {
        EnsureValidScale();

        return new Point(
            HostBoundsDips.X + ((screenPoint.X - HostBoundsPixels.X) / MonitorScaleX),
            HostBoundsDips.Y + ((screenPoint.Y - HostBoundsPixels.Y) / MonitorScaleY));
    }

    public Rect MapScreenPixelRectToHostDips(Int32Rect screenRect)
    {
        EnsureValidScale();

        return new Rect(
            HostBoundsDips.X + ((screenRect.X - HostBoundsPixels.X) / MonitorScaleX),
            HostBoundsDips.Y + ((screenRect.Y - HostBoundsPixels.Y) / MonitorScaleY),
            screenRect.Width / MonitorScaleX,
            screenRect.Height / MonitorScaleY);
    }

    public Int32Rect MapHostDipRectToScreenPixels(Rect hostRect)
    {
        EnsureValidScale();

        return new Int32Rect(
            (int)Math.Round(HostBoundsPixels.X + ((hostRect.X - HostBoundsDips.X) * MonitorScaleX)),
            (int)Math.Round(HostBoundsPixels.Y + ((hostRect.Y - HostBoundsDips.Y) * MonitorScaleY)),
            Math.Max(1, (int)Math.Round(hostRect.Width * MonitorScaleX)),
            Math.Max(1, (int)Math.Round(hostRect.Height * MonitorScaleY)));
    }

    public Int32Rect MapCaptureLocalDipRectToScreenPixels(Rect captureLocalRect)
    {
        var hostRect = new Rect(
            CaptureRectDips.X + captureLocalRect.X,
            CaptureRectDips.Y + captureLocalRect.Y,
            captureLocalRect.Width,
            captureLocalRect.Height);

        return MapHostDipRectToScreenPixels(hostRect);
    }

    public Rect GetCaptureCanvasRectDips() => CaptureRectDips;

    public Rect GetRecordingBorderRectDips(double borderOffset)
    {
        return new Rect(
            CaptureRectDips.Left - borderOffset,
            CaptureRectDips.Top - borderOffset,
            CaptureRectDips.Width + (borderOffset * 2d),
            CaptureRectDips.Height + (borderOffset * 2d));
    }

    public bool IsScreenPixelPointInsideCapture(Point screenPoint)
    {
        return CaptureRectDips.Contains(MapScreenPixelPointToHostDips(screenPoint));
    }

    public bool IsScreenPixelPointInsideHostRect(Point screenPoint, Rect hostRect)
    {
        return hostRect.Contains(MapScreenPixelPointToHostDips(screenPoint));
    }

    private void EnsureValidScale()
    {
        if (MonitorScaleX <= 0d || MonitorScaleY <= 0d)
        {
            throw new InvalidOperationException("Recording session geometry must use positive monitor scales.");
        }
    }
}
