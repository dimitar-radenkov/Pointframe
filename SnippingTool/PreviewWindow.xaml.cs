using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using Brushes = System.Windows.Media.Brushes;
using Clipboard = System.Windows.Clipboard;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using RadioButton = System.Windows.Controls.RadioButton;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using Size = System.Windows.Size;
using TextBox = System.Windows.Controls.TextBox;

namespace SnippingTool;

public enum AnnotationTool { Arrow, Rectangle, Text, Highlight, Pen, Line, Circle }

public partial class PreviewWindow : Window
{
    private AnnotationTool _activeTool = AnnotationTool.Arrow;
    private Color _activeColor = Colors.Red;
    private double _strokeThickness = 2;

    private bool _isDragging;
    private Point _dragStart;

    // In-progress shape references
    private Line? _arrowLine;
    private Polyline? _arrowHead;
    private System.Windows.Shapes.Rectangle? _currentRect;
    private Line? _currentLine;
    private Ellipse? _currentEllipse;
    private Polyline? _currentPen;

    // Undo/Redo — each entry is the group of UIElements added in one stroke
    private readonly List<List<UIElement>> _undoStack = new();
    private readonly List<List<UIElement>> _redoStack = new();
    private List<UIElement>? _currentGroup;

    public PreviewWindow(BitmapSource bitmap, System.Windows.Rect snipScreenRect = default)
    {
        InitializeComponent();

        SnipImage.Source = bitmap;
        AnnotationCanvas.Width = bitmap.PixelWidth;
        AnnotationCanvas.Height = bitmap.PixelHeight;

        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(bitmap.PixelWidth  + 16, workArea.Width  - 40);
        Height = Math.Min(bitmap.PixelHeight + 64, workArea.Height - 40);

        if (!snipScreenRect.IsEmpty)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            PositionNearSnip(snipScreenRect, workArea);
        }
    }

    // Place the preview window just below (or above) the snipped region
    private void PositionNearSnip(System.Windows.Rect snip, System.Windows.Rect workArea)
    {
        const double gap = 8;

        double left = snip.Left;
        double top = snip.Bottom + gap;

        // Keep right edge inside work area
        if (left + Width > workArea.Right)
            left = workArea.Right - Width;
        left = Math.Max(workArea.Left, left);

        // If it doesn't fit below, flip above the snip
        if (top + Height > workArea.Bottom)
            top = snip.Top - Height - gap;

        // Final vertical clamp
        top = Math.Max(workArea.Top, top);

        Left = left;
        Top = top;
    }

    // ─── Window chrome ────────────────────────────────────────────────────

    private void Toolbar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); return; }

        if (e.KeyboardDevice.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.C: Copy_Click(this, new RoutedEventArgs()); break;
                case Key.S: Save_Click(this, new RoutedEventArgs()); break;
                case Key.Z: PerformUndo(); break;
                case Key.Y: PerformRedo(); break;
            }
        }
    }

    // ─── Tool & colour selection ──────────────────────────────────────────

    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string tag)
            _activeTool = Enum.Parse<AnnotationTool>(tag);
    }

    private void Color_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border) return;

        _activeColor = border.Tag switch
        {
            "Red"    => Colors.Red, "Blue"   => Colors.DodgerBlue, "Black"  => Color.FromRgb(0x1A, 0x1A, 0x1A), "Green"  => Color.FromRgb(0x22, 0xA4, 0x22), "Orange" => Colors.Orange, "Purple" => Color.FromRgb(0x8B, 0x2B, 0xE2), "White"  => Colors.White, "Pink"   => Colors.HotPink, _ => Colors.Red
        };

        ColorIndicator.Fill = new SolidColorBrush(_activeColor);
        ColorPopup.IsOpen = false;
    }

    private void ColorPickerBtn_Click(object sender, RoutedEventArgs e)
    {
        ColorPopup.IsOpen = !ColorPopup.IsOpen;
    }

    private void ColorMoreBtn_Click(object sender, RoutedEventArgs e)
    {
        ColorPopup.IsOpen = false;

        var dlg = new System.Windows.Forms.ColorDialog
        {
            Color = System.Drawing.Color.FromArgb(
                _activeColor.A, _activeColor.R, _activeColor.G, _activeColor.B), FullOpen = true
        };

        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            var c = dlg.Color;
            _activeColor = Color.FromArgb(c.A, c.R, c.G, c.B);
            ColorIndicator.Fill = new SolidColorBrush(_activeColor);
        }
    }

    private void StrokeThickness_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (StrokeThickness.SelectedItem is ComboBoxItem item)
        {
            var text = item.Content?.ToString()?.Split(' ')[0];
            if (double.TryParse(text, out var t))
                _strokeThickness = t;
        }
    }

    // ─── Undo / Redo ──────────────────────────────────────────────────────

    private void BeginGroup() => _currentGroup = new List<UIElement>();

    private void CommitGroup()
    {
        if (_currentGroup is { Count: > 0 })
        {
            _undoStack.Add(_currentGroup);
            _redoStack.Clear();
        }
        _currentGroup = null;
    }

    private void AddToCanvas(UIElement element)
    {
        AnnotationCanvas.Children.Add(element);
        _currentGroup?.Add(element);
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => PerformUndo();
    private void Redo_Click(object sender, RoutedEventArgs e) => PerformRedo();

    private void PerformUndo()
    {
        if (_undoStack.Count == 0) return;
        var group = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        foreach (var el in group) AnnotationCanvas.Children.Remove(el);
        _redoStack.Add(group);
    }

    private void PerformRedo()
    {
        if (_redoStack.Count == 0) return;
        var group = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        foreach (var el in group) AnnotationCanvas.Children.Add(el);
        _undoStack.Add(group);
    }

    // ─── Canvas mouse events ──────────────────────────────────────────────

    private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(AnnotationCanvas);
        _isDragging = true;
        AnnotationCanvas.CaptureMouse();
        BeginGroup();

        if (_activeTool == AnnotationTool.Text)
        {
            PlaceTextBox(_dragStart);
            _isDragging = false;
            AnnotationCanvas.ReleaseMouseCapture();
            CommitGroup();
            return;
        }

        var brush = new SolidColorBrush(_activeColor);

        switch (_activeTool)
        {
            case AnnotationTool.Arrow:
                _arrowLine = new Line
                {
                    X1 = _dragStart.X, Y1 = _dragStart.Y, X2 = _dragStart.X, Y2 = _dragStart.Y, Stroke = brush, StrokeThickness = _strokeThickness, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
                };
                _arrowHead = new Polyline
                {
                    Stroke = brush, StrokeThickness = _strokeThickness, StrokeLineJoin = PenLineJoin.Round
                };
                AddToCanvas(_arrowLine);
                AddToCanvas(_arrowHead);
                break;

            case AnnotationTool.Rectangle:
                _currentRect = new System.Windows.Shapes.Rectangle
                {
                    Stroke = brush, StrokeThickness = _strokeThickness, Fill = Brushes.Transparent
                };
                Canvas.SetLeft(_currentRect, _dragStart.X);
                Canvas.SetTop(_currentRect, _dragStart.Y);
                AddToCanvas(_currentRect);
                break;

            case AnnotationTool.Highlight:
                _currentRect = new System.Windows.Shapes.Rectangle
                {
                    Fill = new SolidColorBrush(Color.FromArgb(80, 255, 255, 0)), Stroke = Brushes.Transparent
                };
                Canvas.SetLeft(_currentRect, _dragStart.X);
                Canvas.SetTop(_currentRect, _dragStart.Y);
                AddToCanvas(_currentRect);
                break;

            case AnnotationTool.Pen:
                _currentPen = new Polyline
                {
                    Stroke = brush, StrokeThickness = _strokeThickness, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
                };
                _currentPen.Points.Add(_dragStart);
                AddToCanvas(_currentPen);
                break;

            case AnnotationTool.Line:
                _currentLine = new Line
                {
                    X1 = _dragStart.X, Y1 = _dragStart.Y, X2 = _dragStart.X, Y2 = _dragStart.Y, Stroke = brush, StrokeThickness = _strokeThickness, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
                };
                AddToCanvas(_currentLine);
                break;

            case AnnotationTool.Circle:
                _currentEllipse = new Ellipse
                {
                    Stroke = brush, StrokeThickness = _strokeThickness, Fill = Brushes.Transparent
                };
                Canvas.SetLeft(_currentEllipse, _dragStart.X);
                Canvas.SetTop(_currentEllipse, _dragStart.Y);
                AddToCanvas(_currentEllipse);
                break;
        }
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging) return;

        var current = e.GetPosition(AnnotationCanvas);

        switch (_activeTool)
        {
            case AnnotationTool.Arrow when _arrowLine != null:
                _arrowLine.X2 = current.X;
                _arrowLine.Y2 = current.Y;
                UpdateArrowHead(_arrowLine, _arrowHead!);
                break;

            case AnnotationTool.Rectangle when _currentRect != null:
            case AnnotationTool.Highlight  when _currentRect != null:
                UpdateRect(_currentRect, _dragStart, current);
                break;

            case AnnotationTool.Circle when _currentEllipse != null:
                UpdateEllipse(_currentEllipse, _dragStart, current);
                break;

            case AnnotationTool.Line when _currentLine != null:
                _currentLine.X2 = current.X;
                _currentLine.Y2 = current.Y;
                break;

            case AnnotationTool.Pen when _currentPen != null:
                _currentPen.Points.Add(current);
                break;
        }
    }

    private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;

        _isDragging = false;
        AnnotationCanvas.ReleaseMouseCapture();

        if (_activeTool == AnnotationTool.Arrow && _arrowLine != null)
            UpdateArrowHead(_arrowLine, _arrowHead!);

        _arrowLine = null;
        _arrowHead = null;
        _currentRect = null;
        _currentLine = null;
        _currentEllipse = null;
        _currentPen = null;

        CommitGroup();
    }

    // ─── Shape helpers ────────────────────────────────────────────────────

    private static void UpdateRect(
        System.Windows.Shapes.Rectangle rect, Point start, Point end)
    {
        Canvas.SetLeft(rect, Math.Min(start.X, end.X));
        Canvas.SetTop(rect, Math.Min(start.Y, end.Y));
        rect.Width = Math.Abs(end.X - start.X);
        rect.Height = Math.Abs(end.Y - start.Y);
    }

    private static void UpdateEllipse(Ellipse ellipse, Point start, Point end)
    {
        Canvas.SetLeft(ellipse, Math.Min(start.X, end.X));
        Canvas.SetTop(ellipse, Math.Min(start.Y, end.Y));
        ellipse.Width = Math.Abs(end.X - start.X);
        ellipse.Height = Math.Abs(end.Y - start.Y);
    }

    private static void UpdateArrowHead(Line line, Polyline head)
    {
        const double headLen = 14;
        const double angle = 25 * Math.PI / 180;

        double dx = line.X2 - line.X1;
        double dy = line.Y2 - line.Y1;
        double theta = Math.Atan2(dy, dx);

        var p1 = new Point(
            line.X2 - headLen * Math.Cos(theta - angle), line.Y2 - headLen * Math.Sin(theta - angle));
        var p2 = new Point(
            line.X2 - headLen * Math.Cos(theta + angle), line.Y2 - headLen * Math.Sin(theta + angle));

        head.Points.Clear();
        head.Points.Add(p1);
        head.Points.Add(new Point(line.X2, line.Y2));
        head.Points.Add(p2);
    }

    private void PlaceTextBox(Point position)
    {
        var tb = new TextBox
        {
            FontSize = 16, Foreground = new SolidColorBrush(_activeColor), Background = Brushes.Transparent, BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray, MinWidth = 80, AcceptsReturn = false
        };

        tb.LostFocus += (_, _) =>
        {
            tb.IsReadOnly = true;
            tb.BorderThickness = new Thickness(0);
            tb.Cursor = Cursors.Arrow;
        };

        Canvas.SetLeft(tb, position.X);
        Canvas.SetTop(tb, position.Y);
        AddToCanvas(tb);
        tb.Focus();
    }

    // ─── Export: Copy ─────────────────────────────────────────────────────

    private void Copy_Click(object sender, RoutedEventArgs e) =>
        Clipboard.SetImage(RenderComposite());

    // ─── Export: Save ─────────────────────────────────────────────────────

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Save Screenshot", Filter = "PNG Image|*.png|JPEG Image|*.jpg", DefaultExt = ".png", FileName = $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}"
        };

        if (dlg.ShowDialog() != true) return;

        var rtb = RenderComposite();
        BitmapEncoder encoder = dlg.FilterIndex == 2
            ? new JpegBitmapEncoder { QualityLevel = 95 }
            : new PngBitmapEncoder();

        encoder.Frames.Add(BitmapFrame.Create(rtb));

        using var fs = new System.IO.FileStream(dlg.FileName, System.IO.FileMode.Create);
        encoder.Save(fs);
    }

    // ─── Composite renderer ───────────────────────────────────────────────

    private RenderTargetBitmap RenderComposite()
    {
        RootGrid.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        RootGrid.Arrange(new Rect(RootGrid.DesiredSize));

        var rtb = new RenderTargetBitmap(
            (int)AnnotationCanvas.Width, (int)AnnotationCanvas.Height, 96, 96, PixelFormats.Pbgra32);

        rtb.Render(RootGrid);
        rtb.Freeze();

        return rtb;
    }
}