using System.Windows;

namespace SnippingTool;

internal enum OverlayActionBarMode
{
    Full,
    Compact
}

internal sealed record OverlayToolbarLayout(Rect ToolBounds, Rect ActionBounds, OverlayActionBarMode ActionBarMode);

internal static class OverlayToolbarLayoutHelper
{
    private const double OverlayPadding = 16d;
    private const double SelectionGap = 8d;
    private const double PlacementGap = 9d;
    private const double ToolbarSpacing = 12d;

    public static OverlayToolbarLayout Calculate(
        Rect selectionRect,
        Size overlaySize,
        Size toolSize,
        Size fullActionSize,
        Size compactActionSize)
    {
        var toolCandidates = GetToolCandidates(selectionRect, overlaySize, toolSize).ToArray();
        var preferCompactActionBar = selectionRect.Width < fullActionSize.Width;

        if (!preferCompactActionBar)
        {
            foreach (var toolBounds in toolCandidates)
            {
                foreach (var actionBounds in GetActionCandidates(selectionRect, overlaySize, fullActionSize, allowDockedCandidates: false))
                {
                    if (IsValidLayout(selectionRect, overlaySize, toolBounds, actionBounds))
                    {
                        return new OverlayToolbarLayout(toolBounds, actionBounds, OverlayActionBarMode.Full);
                    }
                }
            }
        }

        foreach (var toolBounds in toolCandidates)
        {
            foreach (var actionBounds in GetActionCandidates(selectionRect, overlaySize, compactActionSize, allowDockedCandidates: true))
            {
                if (IsValidLayout(selectionRect, overlaySize, toolBounds, actionBounds))
                {
                    return new OverlayToolbarLayout(toolBounds, actionBounds, OverlayActionBarMode.Compact);
                }
            }
        }

        var fallbackToolBounds = toolCandidates[0];
        var fallbackActionBounds = ClampRectToOverlay(
            new Rect(
                selectionRect.Left,
                overlaySize.Height - compactActionSize.Height - OverlayPadding,
                compactActionSize.Width,
                compactActionSize.Height),
            overlaySize);

        return new OverlayToolbarLayout(fallbackToolBounds, fallbackActionBounds, OverlayActionBarMode.Compact);
    }

    private static IEnumerable<Rect> GetToolCandidates(Rect selectionRect, Size overlaySize, Size toolSize)
    {
        var rightBounds = TryCreateToolBounds(selectionRect.Right + PlacementGap, selectionRect.Top, toolSize, overlaySize);
        if (rightBounds.HasValue)
        {
            yield return rightBounds.Value;
        }

        var leftBounds = TryCreateToolBounds(selectionRect.Left - toolSize.Width - PlacementGap, selectionRect.Top, toolSize, overlaySize);
        if (leftBounds.HasValue)
        {
            yield return leftBounds.Value;
        }

        yield return ClampRectToOverlay(
            new Rect(
                overlaySize.Width - toolSize.Width - OverlayPadding,
                selectionRect.Top,
                toolSize.Width,
                toolSize.Height),
            overlaySize);

        yield return ClampRectToOverlay(
            new Rect(
                OverlayPadding,
                selectionRect.Top,
                toolSize.Width,
                toolSize.Height),
            overlaySize);
    }

    private static Rect? TryCreateToolBounds(double left, double top, Size toolSize, Size overlaySize)
    {
        if (left < OverlayPadding || left + toolSize.Width > overlaySize.Width - OverlayPadding)
        {
            return null;
        }

        return ClampRectToOverlay(new Rect(left, top, toolSize.Width, toolSize.Height), overlaySize);
    }

    private static IEnumerable<Rect> GetActionCandidates(Rect selectionRect, Size overlaySize, Size actionSize, bool allowDockedCandidates)
    {
        foreach (var top in new[]
                 {
                     selectionRect.Bottom + PlacementGap,
                     selectionRect.Top - actionSize.Height - PlacementGap
                 })
        {
            foreach (var left in GetAnchoredLeftPositions(selectionRect, overlaySize.Width, actionSize.Width))
            {
                yield return ClampRectToOverlay(new Rect(left, top, actionSize.Width, actionSize.Height), overlaySize);
            }
        }

        if (!allowDockedCandidates)
        {
            yield break;
        }

        foreach (var top in new[]
                 {
                     OverlayPadding,
                     overlaySize.Height - actionSize.Height - OverlayPadding
                 })
        {
            foreach (var left in GetAnchoredLeftPositions(selectionRect, overlaySize.Width, actionSize.Width, includeOverlayCenter: true))
            {
                yield return ClampRectToOverlay(new Rect(left, top, actionSize.Width, actionSize.Height), overlaySize);
            }
        }
    }

    private static IEnumerable<double> GetAnchoredLeftPositions(Rect selectionRect, double overlayWidth, double actionWidth, bool includeOverlayCenter = false)
    {
        var maxLeft = Math.Max(OverlayPadding, overlayWidth - actionWidth - OverlayPadding);

        yield return Clamp(selectionRect.Left + ((selectionRect.Width - actionWidth) / 2d), OverlayPadding, maxLeft);
        yield return Clamp(selectionRect.Left, OverlayPadding, maxLeft);
        yield return Clamp(selectionRect.Right - actionWidth, OverlayPadding, maxLeft);

        if (includeOverlayCenter)
        {
            yield return Clamp((overlayWidth - actionWidth) / 2d, OverlayPadding, maxLeft);
        }
    }

    private static bool IsValidLayout(Rect selectionRect, Size overlaySize, Rect toolBounds, Rect actionBounds)
    {
        return IsWithinOverlay(toolBounds, overlaySize)
            && IsWithinOverlay(actionBounds, overlaySize)
            && !IntersectsWithPadding(toolBounds, selectionRect, SelectionGap)
            && !IntersectsWithPadding(actionBounds, selectionRect, SelectionGap)
            && !IntersectsWithPadding(toolBounds, actionBounds, ToolbarSpacing / 2d);
    }

    private static bool IsWithinOverlay(Rect bounds, Size overlaySize)
    {
        return bounds.Left >= 0
            && bounds.Top >= 0
            && bounds.Right <= overlaySize.Width
            && bounds.Bottom <= overlaySize.Height;
    }

    private static bool IntersectsWithPadding(Rect first, Rect second, double padding)
    {
        var paddedFirst = first;
        paddedFirst.Inflate(padding, padding);
        return paddedFirst.IntersectsWith(second);
    }

    private static Rect ClampRectToOverlay(Rect bounds, Size overlaySize)
    {
        var maxLeft = Math.Max(0d, overlaySize.Width - bounds.Width - OverlayPadding);
        var maxTop = Math.Max(0d, overlaySize.Height - bounds.Height - OverlayPadding);
        var left = Clamp(bounds.Left, OverlayPadding, maxLeft);
        var top = Clamp(bounds.Top, OverlayPadding, maxTop);
        return new Rect(left, top, bounds.Width, bounds.Height);
    }

    private static double Clamp(double value, double min, double max)
    {
        if (max < min)
        {
            return min;
        }

        return Math.Max(min, Math.Min(value, max));
    }
}
