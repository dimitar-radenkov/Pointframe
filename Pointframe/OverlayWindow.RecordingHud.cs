using System.Windows;

namespace Pointframe;

public partial class OverlayWindow
{
    internal static (double Left, double Top) ComputeRecordingHudPosition(
        Rect region,
        double hudWidth,
        double hudHeight,
        Rect workArea,
        int gapPixels = 8)
    {
        var left = Math.Max(workArea.Left, Math.Min(region.Left + ((region.Width - hudWidth) / 2d), workArea.Right - hudWidth));
        var top = Math.Min(region.Bottom + gapPixels, workArea.Bottom - hudHeight);
        return (left, top);
    }

    private void RecordingHudPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
    }

    private void RecordingToolButton_Checked(object sender, RoutedEventArgs e)
    {
    }
}
