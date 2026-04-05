using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SnippingTool;

internal sealed class SelectionBackdropWindow : Window
{
    private const byte DimOpacity = 128;

    internal SelectionBackdropWindow(BitmapSource snapshot, Rect bounds)
    {
        Title = nameof(SelectionBackdropWindow);
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = false;
        Background = Brushes.Black;
        ShowInTaskbar = false;
        Topmost = false;
        ShowActivated = false;
        Focusable = false;
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;

        Content = new System.Windows.Controls.Image
        {
            Source = CreateDimmedSnapshot(snapshot),
            Stretch = Stretch.Fill,
            Width = bounds.Width,
            Height = bounds.Height,
            IsHitTestVisible = false
        };
    }

    internal static BitmapSource CreateDimmedSnapshot(BitmapSource snapshot)
    {
        var drawingVisual = new DrawingVisual();
        using (var drawingContext = drawingVisual.RenderOpen())
        {
            var bounds = new Rect(0, 0, snapshot.PixelWidth, snapshot.PixelHeight);
            drawingContext.DrawImage(snapshot, bounds);
            drawingContext.DrawRectangle(
                new SolidColorBrush(Color.FromArgb(DimOpacity, 0, 0, 0)),
                null,
                bounds);
        }

        var dimmedSnapshot = new RenderTargetBitmap(
            snapshot.PixelWidth,
            snapshot.PixelHeight,
            snapshot.DpiX,
            snapshot.DpiY,
            PixelFormats.Pbgra32);
        dimmedSnapshot.Render(drawingVisual);
        dimmedSnapshot.Freeze();
        return dimmedSnapshot;
    }
}
