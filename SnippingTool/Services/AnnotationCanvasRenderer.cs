using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Extensions.Logging;
using SnippingTool.Models;
using SnippingTool.ViewModels;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using Point = System.Windows.Point;
using TextBox = System.Windows.Controls.TextBox;

namespace SnippingTool.Services;

internal sealed class AnnotationCanvasRenderer
{
    private const int BlurRadius = 15;

    private readonly Canvas _canvas;
    private readonly AnnotationViewModel _vm;
    private readonly Action<UIElement> _onAdd;
    private readonly ILogger<AnnotationCanvasRenderer> _logger;

    private Line? _arrowShaft;
    private Polyline? _arrowHead;
    private Line? _currentLine;
    private System.Windows.Shapes.Rectangle? _currentRect;
    private Ellipse? _currentEllipse;
    private Polyline? _currentPen;
    private System.Windows.Shapes.Rectangle? _blurDraft;
    private System.Windows.Shapes.Rectangle? _calloutBubble;
    private Polygon? _calloutTail;

    private BitmapSource? _backgroundCapture;
    private double _dpiX = 1.0;
    private double _dpiY = 1.0;

    public BitmapSource? BackgroundCapture => _backgroundCapture;

    public void SetBackground(BitmapSource background, double dpiX, double dpiY)
    {
        _backgroundCapture = background;
        _dpiX = dpiX;
        _dpiY = dpiY;
    }

    public AnnotationCanvasRenderer(
        Canvas canvas,
        AnnotationViewModel vm,
        Action<UIElement> onAdd,
        ILogger<AnnotationCanvasRenderer> logger)
    {
        _canvas = canvas;
        _vm = vm;
        _onAdd = onAdd;
        _logger = logger;
    }

    private SolidColorBrush ActiveBrush() => new(_vm.ActiveColor);

    public void BeginShape(Point p)
    {
        _logger.LogDebug("Shape begin: {Tool}", _vm.SelectedTool);
        var brush = ActiveBrush();
        var thick = _vm.StrokeThickness;

        switch (_vm.SelectedTool)
        {
            case AnnotationTool.Arrow:
                _arrowShaft = new Line { X1 = p.X, Y1 = p.Y, X2 = p.X, Y2 = p.Y, Stroke = brush, StrokeThickness = thick, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                _arrowHead = new Polyline { Stroke = brush, StrokeThickness = thick, StrokeLineJoin = PenLineJoin.Round, StrokeEndLineCap = PenLineCap.Round };
                Add(_arrowShaft);
                Add(_arrowHead);
                break;
            case AnnotationTool.Line:
                _currentLine = new Line { X1 = p.X, Y1 = p.Y, X2 = p.X, Y2 = p.Y, Stroke = brush, StrokeThickness = thick, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                Add(_currentLine);
                break;
            case AnnotationTool.Rectangle:
                _currentRect = new System.Windows.Shapes.Rectangle { Stroke = brush, StrokeThickness = thick, Fill = Brushes.Transparent };
                Canvas.SetLeft(_currentRect, p.X);
                Canvas.SetTop(_currentRect, p.Y);
                Add(_currentRect);
                break;
            case AnnotationTool.Highlight:
                var c = _vm.ActiveColor;
                _currentRect = new System.Windows.Shapes.Rectangle { Fill = new SolidColorBrush(Color.FromArgb(100, c.R, c.G, c.B)) };
                Canvas.SetLeft(_currentRect, p.X);
                Canvas.SetTop(_currentRect, p.Y);
                Add(_currentRect);
                break;
            case AnnotationTool.Pen:
                _currentPen = new Polyline { Stroke = brush, StrokeThickness = thick, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                _currentPen.Points.Add(p);
                Add(_currentPen);
                break;
            case AnnotationTool.Circle:
                _currentEllipse = new Ellipse { Stroke = brush, StrokeThickness = thick, Fill = Brushes.Transparent };
                Canvas.SetLeft(_currentEllipse, p.X);
                Canvas.SetTop(_currentEllipse, p.Y);
                Add(_currentEllipse);
                break;
            case AnnotationTool.Blur:
                _blurDraft = new System.Windows.Shapes.Rectangle
                {
                    Fill = new SolidColorBrush(Color.FromArgb(80, 120, 120, 120)),
                    Stroke = Brushes.White,
                    StrokeDashArray = new DoubleCollection { 4, 2 },
                    StrokeThickness = 1
                };
                Canvas.SetLeft(_blurDraft, p.X);
                Canvas.SetTop(_blurDraft, p.Y);
                _canvas.Children.Add(_blurDraft);
                break;
            case AnnotationTool.Callout:
                _calloutBubble = new System.Windows.Shapes.Rectangle
                {
                    RadiusX = 6,
                    RadiusY = 6,
                    Stroke = brush,
                    StrokeThickness = thick,
                    Fill = Brushes.White
                };
                Canvas.SetLeft(_calloutBubble, p.X);
                Canvas.SetTop(_calloutBubble, p.Y);
                _calloutTail = new Polygon
                {
                    Stroke = brush,
                    StrokeThickness = thick,
                    Fill = Brushes.White,
                    StrokeLineJoin = PenLineJoin.Round
                };
                Add(_calloutBubble);
                Add(_calloutTail);
                break;
        }
    }

    public void UpdateShape(Point p)
    {
        var @params = _vm.TryGetShapeParameters();
        if (@params is null)
        {
            return;
        }

        switch (@params)
        {
            case ArrowShapeParameters arr when _arrowShaft is not null && _arrowHead is not null:
                _arrowShaft.X2 = arr.P2.X;
                _arrowShaft.Y2 = arr.P2.Y;
                _arrowHead.Points.Clear();
                foreach (var pt in arr.ArrowHead)
                {
                    _arrowHead.Points.Add(pt);
                }

                break;
            case LineShapeParameters line when _currentLine is not null:
                _currentLine.X2 = line.P2.X;
                _currentLine.Y2 = line.P2.Y;
                break;
            case RectShapeParameters rect when _currentRect is not null:
                Canvas.SetLeft(_currentRect, rect.Left);
                Canvas.SetTop(_currentRect, rect.Top);
                _currentRect.Width = rect.Width;
                _currentRect.Height = rect.Height;
                break;
            case EllipseShapeParameters ellipse when _currentEllipse is not null:
                Canvas.SetLeft(_currentEllipse, ellipse.Left);
                Canvas.SetTop(_currentEllipse, ellipse.Top);
                _currentEllipse.Width = ellipse.Width;
                _currentEllipse.Height = ellipse.Height;
                break;
            case PenShapeParameters when _currentPen is not null:
                _currentPen.Points.Add(p);
                break;
            case BlurShapeParameters blur when _blurDraft is not null:
                Canvas.SetLeft(_blurDraft, blur.Left);
                Canvas.SetTop(_blurDraft, blur.Top);
                _blurDraft.Width = blur.Width;
                _blurDraft.Height = blur.Height;
                break;
            case CalloutShapeParameters c when _calloutBubble is not null && _calloutTail is not null:
                Canvas.SetLeft(_calloutBubble, c.Left);
                Canvas.SetTop(_calloutBubble, c.Top);
                _calloutBubble.Width = c.Width;
                _calloutBubble.Height = c.Height;
                _calloutTail.Points.Clear();
                _calloutTail.Points.Add(new Point(c.Left + c.Width * 0.15, c.Top + c.Height));
                _calloutTail.Points.Add(new Point(c.Left + c.Width * 0.35, c.Top + c.Height));
                _calloutTail.Points.Add(c.Tail);
                break;
        }
    }

    public void CommitShape(Point p)
    {
        _logger.LogDebug("Shape committed: {Tool}", _vm.SelectedTool);

        if (_vm.SelectedTool == AnnotationTool.Blur)
        {
            CommitBlur();
        }
        else
        {
            UpdateShape(p);
        }

        _arrowShaft = null;
        _arrowHead = null;
        _currentLine = null;
        _currentRect = null;
        _currentEllipse = null;
        _currentPen = null;
        _blurDraft = null;
        _calloutBubble = null;
        _calloutTail = null;
    }

    private void CommitBlur()
    {
        var @params = _vm.TryGetShapeParameters() as BlurShapeParameters;
        if (_blurDraft is not null)
        {
            _canvas.Children.Remove(_blurDraft);
        }

        if (@params is null || _backgroundCapture is null)
        {
            return;
        }

        var pixelX = (int)(@params.Left * _dpiX);
        var pixelY = (int)(@params.Top * _dpiY);
        var pixelW = Math.Max(1, (int)(@params.Width * _dpiX));
        var pixelH = Math.Max(1, (int)(@params.Height * _dpiY));

        // Clamp to bitmap bounds
        pixelX = Math.Max(0, Math.Min(pixelX, _backgroundCapture.PixelWidth - 1));
        pixelY = Math.Max(0, Math.Min(pixelY, _backgroundCapture.PixelHeight - 1));
        pixelW = Math.Min(pixelW, _backgroundCapture.PixelWidth - pixelX);
        pixelH = Math.Min(pixelH, _backgroundCapture.PixelHeight - pixelY);

        if (pixelW <= 0 || pixelH <= 0)
        {
            return;
        }

        var cropped = new CroppedBitmap(_backgroundCapture, new Int32Rect(pixelX, pixelY, pixelW, pixelH));
        cropped.Freeze();

        var img = new System.Windows.Controls.Image
        {
            Width = @params.Width,
            Height = @params.Height,
            Source = cropped,
            Stretch = Stretch.Fill,
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = BlurRadius },
        };
        Canvas.SetLeft(img, @params.Left);
        Canvas.SetTop(img, @params.Top);
        Add(img);
    }

    public void PlaceNumberLabel(Point p)
    {
        var n = _vm.IncrementNumberCounter();
        _logger.LogDebug("Number label ({N}) placed at ({X:F1},{Y:F1})", n, p.X, p.Y);
        const double Size = 28;
        var fill = ActiveBrush();
        var badge = new Grid
        {
            Width = Size,
            Height = Size,
            Tag = "number"
        };
        badge.Children.Add(new Ellipse { Fill = fill });
        badge.Children.Add(new TextBlock
        {
            Text = n.ToString(),
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        Canvas.SetLeft(badge, p.X - Size / 2);
        Canvas.SetTop(badge, p.Y - Size / 2);
        Add(badge);
    }

    public void PlaceTextBox(Point p)
    {
        _logger.LogDebug("Text box placed at ({X:F1},{Y:F1})", p.X, p.Y);
        var tb = new TextBox
        {
            FontSize = 16,
            Foreground = ActiveBrush(),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Gray,
            MinWidth = 80,
            AcceptsReturn = false,
            Padding = new Thickness(2)
        };
        tb.LostFocus += (_, _) =>
        {
            tb.IsReadOnly = true;
            tb.BorderThickness = new Thickness(0);
            tb.Cursor = Cursors.Arrow;
        };
        tb.KeyDown += (_, ke) =>
        {
            if (ke.Key is Key.Enter or Key.Escape)
            {
                FinalizeTextBox(tb);
                ke.Handled = true;
            }
        };
        Canvas.SetLeft(tb, p.X);
        Canvas.SetTop(tb, p.Y);
        Add(tb);
        tb.Focus();
    }

    private void FinalizeTextBox(TextBox tb)
    {
        if (!_canvas.Children.Contains(tb))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(tb.Text))
        {
            _canvas.Children.Remove(tb);
            return;
        }

        var pos = new Point(Canvas.GetLeft(tb), Canvas.GetTop(tb));
        var text = tb.Text;
        var brush = tb.Foreground;
        _canvas.Children.Remove(tb);
        var block = new TextBlock { Text = text, Foreground = brush, FontSize = 16, FontWeight = FontWeights.SemiBold };
        Canvas.SetLeft(block, pos.X);
        Canvas.SetTop(block, pos.Y);
        Add(block);
    }

    public void PlaceCalloutTextBox(double left, double top, double width, double height)
    {
        _logger.LogDebug("Callout text box placed at ({X:F1},{Y:F1})", left, top);
        var tb = new TextBox
        {
            FontSize = 14,
            Foreground = ActiveBrush(),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Width = Math.Max(width - 16, 40),
            Padding = new Thickness(2)
        };
        tb.LostFocus += (_, _) => FinalizeCalloutTextBox(tb);
        tb.KeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Escape)
            {
                FinalizeCalloutTextBox(tb);
                ke.Handled = true;
            }
        };
        Canvas.SetLeft(tb, left + 8);
        Canvas.SetTop(tb, top + 8);
        Add(tb);
        tb.Focus();
    }

    private void FinalizeCalloutTextBox(TextBox tb)
    {
        if (!_canvas.Children.Contains(tb))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(tb.Text))
        {
            _canvas.Children.Remove(tb);
            return;
        }

        var left = Canvas.GetLeft(tb);
        var top = Canvas.GetTop(tb);
        var width = tb.Width;
        var text = tb.Text;
        var brush = tb.Foreground;
        _canvas.Children.Remove(tb);
        var block = new TextBlock
        {
            Text = text,
            Foreground = brush,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            Width = width
        };
        Canvas.SetLeft(block, left);
        Canvas.SetTop(block, top);
        Add(block);
    }

    private void Add(UIElement element)
    {
        _canvas.Children.Add(element);
        _onAdd(element);
    }
}
