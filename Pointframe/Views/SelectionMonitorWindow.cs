using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Cursors = System.Windows.Input.Cursors;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace Pointframe;

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
    private readonly ILogger<SelectionMonitorWindow> _logger;
    private Point? _dragStart;

    internal SelectionMonitorWindow(
        string monitorName,
        BitmapSource monitorSnapshot,
        Rect hostBoundsDips,
        Int32Rect hostBoundsPixels,
        double dpiScaleX,
        double dpiScaleY,
        ILogger<SelectionMonitorWindow>? logger = null)
    {
        _monitorName = monitorName;
        _monitorSnapshot = monitorSnapshot;
        _hostBoundsDips = hostBoundsDips;
        _hostBoundsPixels = hostBoundsPixels;
        _dpiScaleX = dpiScaleX;
        _dpiScaleY = dpiScaleY;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SelectionMonitorWindow>.Instance;

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
        _logger.LogDebug(
            "Drag start: monitor={Monitor} start={X:F1},{Y:F1} windowSize={W:F1}×{H:F1}",
            _monitorName,
            _dragStart.Value.X,
            _dragStart.Value.Y,
            _hostBoundsDips.Width,
            _hostBoundsDips.Height);
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

        var rawRight = Math.Max(start.X, end.X);
        var rawBottom = Math.Max(start.Y, end.Y);
        var selectionRect = CreateSelectionRect(start, end);

        if (rawRight > _hostBoundsDips.Width - 1d || rawBottom > _hostBoundsDips.Height - 1d)
        {
            _logger.LogDebug(
                "Selection clamped to monitor bounds: monitor={Monitor} rawEnd={RawX:F1},{RawY:F1} clampedRight={CR:F1} clampedBottom={CB:F1} windowSize={W:F1}×{H:F1}",
                _monitorName,
                rawRight,
                rawBottom,
                selectionRect.Right,
                selectionRect.Bottom,
                _hostBoundsDips.Width,
                _hostBoundsDips.Height);
        }

        if (selectionRect.Width < MinimumSelectionSize || selectionRect.Height < MinimumSelectionSize)
        {
            _logger.LogDebug(
                "Selection too small, cancelling: monitor={Monitor} size={W:F1}×{H:F1} minimum={Min}",
                _monitorName,
                selectionRect.Width,
                selectionRect.Height,
                MinimumSelectionSize);
            SelectionCanceled?.Invoke();
            e.Handled = true;
            base.OnMouseLeftButtonUp(e);
            return;
        }

        var selectionBoundsPixels = GetScreenPixelBounds(selectionRect);
        var selectionBackground = CreateSelectionBackground(selectionBoundsPixels);

        _logger.LogDebug(
            "Selection committed: monitor={Monitor} selectionDips={SX:F1},{SY:F1},{SW:F1},{SH:F1} selectionPx={PX},{PY},{PW},{PH}",
            _monitorName,
            selectionRect.X,
            selectionRect.Y,
            selectionRect.Width,
            selectionRect.Height,
            selectionBoundsPixels.X,
            selectionBoundsPixels.Y,
            selectionBoundsPixels.Width,
            selectionBoundsPixels.Height);

        SelectionCompleted?.Invoke(new SelectionSessionResult(
            _monitorName,
            _monitorSnapshot,
            selectionBackground,
            _hostBoundsDips,
            _hostBoundsPixels,
            selectionRect,
            selectionBoundsPixels,
            _dpiScaleX,
            _dpiScaleY,
            SelectionSessionMode.Region));

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

    private Rect CreateSelectionRect(Point start, Point end)
    {
        var left = Math.Max(0d, Math.Min(start.X, end.X));
        var top = Math.Max(0d, Math.Min(start.Y, end.Y));
        var right = Math.Min(_hostBoundsDips.Width - 1d, Math.Max(start.X, end.X));
        var bottom = Math.Min(_hostBoundsDips.Height - 1d, Math.Max(start.Y, end.Y));
        return new Rect(left, top, Math.Max(0d, right - left), Math.Max(0d, bottom - top));
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
