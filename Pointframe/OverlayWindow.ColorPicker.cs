using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Pointframe;

public partial class OverlayWindow
{
    private const int LoupeHalfPixels = 5; // samples an 11×11 pixel grid

    internal void UpdateLoupe(Point? dipPoint)
    {
        if (dipPoint is null || _vm.SelectedTool != AnnotationTool.ColorPicker)
        {
            ColorPickerLoupe.Visibility = Visibility.Collapsed;
            return;
        }

        var crop = _renderer.CropLoupeRegion(dipPoint.Value, LoupeHalfPixels);
        if (crop is null)
        {
            ColorPickerLoupe.Visibility = Visibility.Collapsed;
            return;
        }

        LoupeImage.Source = crop;

        var color = _renderer.SamplePixelColor(dipPoint.Value);
        if (color.HasValue)
        {
            LoupeSwatch.Fill = new SolidColorBrush(color.Value);
            LoupeHexLabel.Text = $"#{color.Value.R:X2}{color.Value.G:X2}{color.Value.B:X2}";
        }

        // Position loupe near cursor, flip if near an edge
        const double Offset = 18;
        const double LoupeW = 120;
        const double LoupeH = 140;

        var lx = dipPoint.Value.X + Offset;
        var ly = dipPoint.Value.Y - LoupeH - Offset;
        if (ly < 0)
        {
            ly = dipPoint.Value.Y + Offset;
        }

        if (lx + LoupeW > ActualWidth)
        {
            lx = dipPoint.Value.X - LoupeW - Offset;
        }

        Canvas.SetLeft(ColorPickerLoupe, lx);
        Canvas.SetTop(ColorPickerLoupe, ly);
        ColorPickerLoupe.Visibility = Visibility.Visible;
    }

    internal void SyncToolbarToSelectedTool()
    {
        foreach (var child in AnnotToolbarStack.Children.OfType<System.Windows.Controls.RadioButton>())
        {
            if (child.Tag is string tag)
            {
                child.IsChecked = tag == _vm.SelectedTool.ToString();
            }
        }
    }
}
