using System.Windows;

namespace Pointframe.Models;

// Pure placement of the recording camera bubble. Works in physical screen pixels (what the recorder captures) and maps
// the result back to host DIPs through RecordingSessionGeometry, so mixed DPI, negative monitor origins and the even-size
// truncation of the capture rectangle stay correct.
//
// Rule: the bubble sits MarginDips inside the chosen corner of the captured rectangle. Its diameter is the chosen size
// capped at one third of the captured rectangle's shorter side; below MinDiameterDips it is omitted. When the recording
// HUD overlaps that corner, the bubble slides along the side edge (vertically) away from the HUD; if that does not fit it
// slides along the top or bottom edge (horizontally) away from the HUD; if neither fits it is omitted.
public static class CameraBubblePlacement
{
    public const double MarginDips = 16d;
    public const double HudGapDips = 8d;
    public const double MinDiameterDips = 64d;
    public const double SmallDiameterDips = 128d;
    public const double LargeDiameterDips = 192d;

    public static double GetRequestedDiameterDips(CameraBubbleSize size)
    {
        return size == CameraBubbleSize.Large ? LargeDiameterDips : SmallDiameterDips;
    }

    public static Rect? Calculate(
        RecordingSessionGeometry geometry,
        CameraBubbleSize size,
        CameraBubbleCorner corner,
        Rect? hudHostRectDips)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (geometry.IsEmpty || geometry.MonitorScaleX <= 0d || geometry.MonitorScaleY <= 0d)
        {
            return null;
        }

        // The recorder truncates width and height to even numbers; the last odd row or column is not in the video.
        var captureX = geometry.CaptureBoundsPixels.X;
        var captureY = geometry.CaptureBoundsPixels.Y;
        var captureWidth = geometry.CaptureBoundsPixels.Width & ~1;
        var captureHeight = geometry.CaptureBoundsPixels.Height & ~1;
        if (captureWidth <= 0 || captureHeight <= 0)
        {
            return null;
        }

        var shorterSideDips = Math.Min(captureWidth / geometry.MonitorScaleX, captureHeight / geometry.MonitorScaleY);
        var diameterDips = Math.Min(GetRequestedDiameterDips(size), shorterSideDips / 3d);
        if (diameterDips < MinDiameterDips)
        {
            return null;
        }

        var diameter = Math.Max(1, (int)Math.Round(diameterDips * geometry.MonitorScaleX));
        var marginX = (int)Math.Round(MarginDips * geometry.MonitorScaleX);
        var marginY = (int)Math.Round(MarginDips * geometry.MonitorScaleY);
        var gapX = (int)Math.Round(HudGapDips * geometry.MonitorScaleX);
        var gapY = (int)Math.Round(HudGapDips * geometry.MonitorScaleY);

        var minX = captureX + marginX;
        var maxX = captureX + captureWidth - marginX - diameter;
        var minY = captureY + marginY;
        var maxY = captureY + captureHeight - marginY - diameter;
        if (maxX < minX || maxY < minY)
        {
            return null;
        }

        var isLeft = corner is CameraBubbleCorner.TopLeft or CameraBubbleCorner.BottomLeft;
        var isTop = corner is CameraBubbleCorner.TopLeft or CameraBubbleCorner.TopRight;
        var x = isLeft ? minX : maxX;
        var y = isTop ? minY : maxY;

        if (hudHostRectDips is { } hudDips && !hudDips.IsEmpty && hudDips.Width > 0d && hudDips.Height > 0d)
        {
            var hud = geometry.MapHostDipRectToScreenPixels(hudDips);
            var hudLeft = hud.X - gapX;
            var hudTop = hud.Y - gapY;
            var hudRight = hud.X + hud.Width + gapX;
            var hudBottom = hud.Y + hud.Height + gapY;

            if (Overlaps(x, y, diameter, hudLeft, hudTop, hudRight, hudBottom))
            {
                var slidY = isTop ? hudBottom : hudTop - diameter;
                if (slidY >= minY && slidY <= maxY)
                {
                    y = slidY;
                }
                else
                {
                    var slidX = isLeft ? hudRight : hudLeft - diameter;
                    if (slidX >= minX && slidX <= maxX)
                    {
                        x = slidX;
                    }
                    else
                    {
                        return null;
                    }
                }
            }
        }

        return geometry.MapScreenPixelRectToHostDips(new Int32Rect(x, y, diameter, diameter));
    }

    private static bool Overlaps(int x, int y, int diameter, int left, int top, int right, int bottom)
    {
        return x < right && x + diameter > left && y < bottom && y + diameter > top;
    }
}
