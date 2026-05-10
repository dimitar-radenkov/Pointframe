using System.Windows;

namespace Pointframe;

public partial class OverlayWindow
{
    internal static (double Left, double Top) ComputeRecordingHudPosition(
        Rect region,
        double hudWidth,
        double hudHeight,
        Rect workArea,
        bool preferTopDock,
        int gapPixels = 8)
    {
        var horizontalAnchor = preferTopDock
            ? workArea.Left + ((workArea.Width - hudWidth) / 2d)
            : region.Left + ((region.Width - hudWidth) / 2d);
        var left = Math.Max(workArea.Left, Math.Min(horizontalAnchor, workArea.Right - hudWidth));
        var top = preferTopDock
            ? Math.Max(workArea.Top, Math.Min(workArea.Top + gapPixels, workArea.Bottom - hudHeight))
            : Math.Min(region.Bottom + gapPixels, workArea.Bottom - hudHeight);
        return (left, top);
    }

    private void RecordingHudPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
    }

    private void RecordingToolButton_Checked(object sender, RoutedEventArgs e)
    {
    }
}
