using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cursors = System.Windows.Input.Cursors;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace SnippingTool;

internal sealed class SelectionMonitorWindow : Window
{
    private const int MinimumSelectionSize = 4;

    private readonly Canvas _root;
    private readonly Rectangle _selectionBorder;
    private readonly Border _sizeLabelBorder;
    private readonly TextBlock _sizeLabelText;
    private readonly BitmapSource _monitorSnapshot;
    private readonly Int32Rect _hostBoundsPixels;
    private readonly Rect _hostBoundsDips;
    private readonly string _monitorName;
    private readonly double _dpiScaleX;
    private readonly double _dpiScaleY;
    private Point? _dragStart;

    internal SelectionMonitorWindow(
        string monitorName,
        BitmapSource monitorSnapshot,
        Rect hostBoundsDips,
        Int32Rect hostBoundsPixels,
        double dpiScaleX,
        double dpiScaleY)
    {
        _monitorName = monitorName;
        _monitorSnapshot = monitorSnapshot;
        _hostBoundsDips = hostBoundsDips;
        _hostBoundsPixels = hostBoundsPixels;
        _dpiScaleX = dpiScaleX;
        _dpiScaleY = dpiScaleY;

        Title = nameof(SelectionMonitorWindow);
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = false;
        Background = Brushes.Black;
        ShowInTaskbar = false;
        Topmost = true;
        Left = hostBoundsDips.Left;
        Top = hostBoundsDips.Top;
        Width = hostBoundsDips.Width;
        Height = hostBoundsDips.Height;
        Cursor = Cursors.Cross;

        _selectionBorder = new Rectangle
        {
            Stroke = Brushes.White,
            StrokeThickness = 1.5,
            StrokeDashArray = [5, 3],
            Fill = Brushes.Transparent,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };

        _sizeLabelText = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 11,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            FontWeight = FontWeights.SemiBold
        };

        _sizeLabelBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(204, 0, 0, 0)),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(5, 2, 5, 2),
            Child = _sizeLabelText,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };

        _root = new Canvas
        {
            Background = Brushes.Transparent,
            Width = hostBoundsDips.Width,
            Height = hostBoundsDips.Height,
            Children =
            {
                new System.Windows.Controls.Image
                {
                    Source = SelectionBackdropWindow.CreateDimmedSnapshot(monitorSnapshot),
                    Stretch = Stretch.Fill,
                    Width = hostBoundsDips.Width,
                    Height = hostBoundsDips.Height,
                    IsHitTestVisible = false
                },
                _selectionBorder,
                _sizeLabelBorder
            }
        };

        Content = _root;
    }

    internal event Action<SelectionSessionResult>? SelectionCompleted;
    internal event Action? SelectionCanceled;

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SelectionCanceled?.Invoke();
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(_root);
        _root.CaptureMouse();
        _selectionBorder.Visibility = Visibility.Visible;
        _sizeLabelBorder.Visibility = Visibility.Visible;
        UpdateSelectionVisual(_dragStart.Value, _dragStart.Value);
        e.Handled = true;
        base.OnMouseLeftButtonDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragStart.HasValue || e.LeftButton != MouseButtonState.Pressed)
        {
            base.OnMouseMove(e);
            return;
        }

        UpdateSelectionVisual(_dragStart.Value, e.GetPosition(_root));
        e.Handled = true;
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragStart.HasValue)
        {
            base.OnMouseLeftButtonUp(e);
            return;
        }

        var start = _dragStart.Value;
        var end = e.GetPosition(_root);
        _dragStart = null;
        _root.ReleaseMouseCapture();

        var selectionRect = CreateSelectionRect(start, end);
        if (selectionRect.Width < MinimumSelectionSize || selectionRect.Height < MinimumSelectionSize)
        {
            SelectionCanceled?.Invoke();
            e.Handled = true;
            base.OnMouseLeftButtonUp(e);
            return;
        }

        var selectionBoundsPixels = GetScreenPixelBounds(selectionRect);
        var selectionBackground = CreateSelectionBackground(selectionBoundsPixels);

        SelectionCompleted?.Invoke(new SelectionSessionResult(
            _monitorName,
            _monitorSnapshot,
            selectionBackground,
            _hostBoundsDips,
            _hostBoundsPixels,
            selectionRect,
            selectionBoundsPixels,
            _dpiScaleX,
            _dpiScaleY));

        e.Handled = true;
        base.OnMouseLeftButtonUp(e);
    }

    private void UpdateSelectionVisual(Point start, Point current)
    {
        var selectionRect = CreateSelectionRect(start, current);
        Canvas.SetLeft(_selectionBorder, selectionRect.X);
        Canvas.SetTop(_selectionBorder, selectionRect.Y);
        _selectionBorder.Width = selectionRect.Width;
        _selectionBorder.Height = selectionRect.Height;

        _sizeLabelText.Text = $"{Math.Round(selectionRect.Width * _dpiScaleX):F0}×{Math.Round(selectionRect.Height * _dpiScaleY):F0}";
        _sizeLabelBorder.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var labelY = selectionRect.Y - _sizeLabelBorder.DesiredSize.Height - 4;
        if (labelY < 0)
        {
            labelY = selectionRect.Y + 4;
        }

        Canvas.SetLeft(_sizeLabelBorder, selectionRect.X);
        Canvas.SetTop(_sizeLabelBorder, labelY);
    }

    private static Rect CreateSelectionRect(Point start, Point end)
    {
        return new Rect(
            Math.Min(start.X, end.X),
            Math.Min(start.Y, end.Y),
            Math.Abs(end.X - start.X),
            Math.Abs(end.Y - start.Y));
    }

    private Int32Rect GetScreenPixelBounds(Rect localRect)
    {
        var x = _hostBoundsPixels.X + (int)Math.Round(localRect.X * _dpiScaleX);
        var y = _hostBoundsPixels.Y + (int)Math.Round(localRect.Y * _dpiScaleY);
        var width = Math.Max(1, (int)Math.Round(localRect.Width * _dpiScaleX));
        var height = Math.Max(1, (int)Math.Round(localRect.Height * _dpiScaleY));
        return new Int32Rect(x, y, width, height);
    }

    private BitmapSource CreateSelectionBackground(Int32Rect selectionBoundsPixels)
    {
        var cropRect = new Int32Rect(
            selectionBoundsPixels.X - _hostBoundsPixels.X,
            selectionBoundsPixels.Y - _hostBoundsPixels.Y,
            selectionBoundsPixels.Width,
            selectionBoundsPixels.Height);
        var croppedBitmap = new CroppedBitmap(_monitorSnapshot, cropRect);
        croppedBitmap.Freeze();
        return croppedBitmap;
    }
}
