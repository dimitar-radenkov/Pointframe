using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using SnippingTool.Models;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Image = System.Windows.Controls.Image;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace SnippingTool.Services.Handlers;

internal sealed class BlurShapeHandler : IAnnotationShapeHandler
{
    private const int BlurRadius = 15;

    private readonly Func<ShapeParameters?> _getShapeParameters;
    private readonly Func<BitmapSource?> _getBackgroundCapture;
    private readonly Func<double> _getDpiX;
    private readonly Func<double> _getDpiY;

    private Rectangle? _draft;

    public BlurShapeHandler(
        Func<ShapeParameters?> getShapeParameters,
        Func<BitmapSource?> getBackgroundCapture,
        Func<double> getDpiX,
        Func<double> getDpiY)
    {
        _getShapeParameters = getShapeParameters;
        _getBackgroundCapture = getBackgroundCapture;
        _getDpiX = getDpiX;
        _getDpiY = getDpiY;
    }

    public void Begin(Point point, SolidColorBrush brush, double thickness, Canvas canvas)
    {
        _draft = new Rectangle
        {
            Fill = new SolidColorBrush(Color.FromArgb(80, 120, 120, 120)),
            Stroke = Brushes.White,
            StrokeDashArray = [4, 2],
            StrokeThickness = 1
        };
        Canvas.SetLeft(_draft, point.X);
        Canvas.SetTop(_draft, point.Y);
        canvas.Children.Add(_draft);
    }

    public void Update(Point point)
    {
        if (_draft is null || _getShapeParameters() is not BlurShapeParameters parameters)
        {
            return;
        }

        Canvas.SetLeft(_draft, parameters.Left);
        Canvas.SetTop(_draft, parameters.Top);
        _draft.Width = parameters.Width;
        _draft.Height = parameters.Height;
    }

    public void Commit(Canvas canvas, Action<UIElement> trackElement)
    {
        if (_draft is not null && canvas.Children.Contains(_draft))
        {
            canvas.Children.Remove(_draft);
        }

        var parameters = _getShapeParameters() as BlurShapeParameters;
        var background = _getBackgroundCapture();
        _draft = null;
        if (parameters is null || background is null)
        {
            return;
        }

        var dpiX = _getDpiX();
        var dpiY = _getDpiY();
        var pixelX = (int)(parameters.Left * dpiX);
        var pixelY = (int)(parameters.Top * dpiY);
        var pixelW = Math.Max(1, (int)(parameters.Width * dpiX));
        var pixelH = Math.Max(1, (int)(parameters.Height * dpiY));

        pixelX = Math.Max(0, Math.Min(pixelX, background.PixelWidth - 1));
        pixelY = Math.Max(0, Math.Min(pixelY, background.PixelHeight - 1));
        pixelW = Math.Min(pixelW, background.PixelWidth - pixelX);
        pixelH = Math.Min(pixelH, background.PixelHeight - pixelY);

        if (pixelW <= 0 || pixelH <= 0)
        {
            return;
        }

        var cropped = new CroppedBitmap(background, new Int32Rect(pixelX, pixelY, pixelW, pixelH));
        cropped.Freeze();

        var image = new Image
        {
            Width = parameters.Width,
            Height = parameters.Height,
            Source = cropped,
            Stretch = Stretch.Fill,
            Effect = new BlurEffect { Radius = BlurRadius }
        };
        Canvas.SetLeft(image, parameters.Left);
        Canvas.SetTop(image, parameters.Top);
        canvas.Children.Add(image);
        trackElement(image);
    }

    public void Cancel(Canvas canvas)
    {
        if (_draft is not null && canvas.Children.Contains(_draft))
        {
            canvas.Children.Remove(_draft);
        }

        _draft = null;
    }
}
