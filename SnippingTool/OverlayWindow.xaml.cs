using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using TextBox = System.Windows.Controls.TextBox;

namespace SnippingTool;

public partial class OverlayWindow : Window
{
    // ── Phase state ────────────────────────────────────────────────────────

    private enum Phase { Selecting, Annotating }
    private Phase _phase = Phase.Selecting;

    // ── Selection ──────────────────────────────────────────────────────────

    private Point _selStart;
    private bool _isDragging;
    private Rect _selection; // WPF DIPs, relative to the virtual screen origin

    // ── DPI ────────────────────────────────────────────────────────────────

    private double _dpiX = 1.0;
    private double _dpiY = 1.0;

    // ── Annotation ────────────────────────────────────────────────────────

    private AnnotationTool _tool = AnnotationTool.Arrow;
    private Color _color = Colors.Red;
    private const double StrokeThick = 2.5;

    private bool _annotDragging;
    private Point _annotStart;

    // In-progress shape references (cleared after each stroke)
    private Line? _arrowShaft;
    private Polyline? _arrowHead;
    private Line? _currentLine;
    private System.Windows.Shapes.Rectangle? _currentRect;
    private Ellipse? _currentEllipse;
    private Polyline? _currentPen;

    public OverlayWindow()
    {
        InitializeComponent();

        // Wire phase-1 canvas mouse events in code so XAML stays clean
        Root.MouseLeftButtonDown += Root_MouseDown;
        Root.MouseMove           += Root_MouseMove;
        Root.MouseLeftButtonUp   += Root_MouseUp;
        KeyDown                  += Window_KeyDown;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Cover entire virtual desktop (all monitors)
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        DimFull.Width = Width;
        DimFull.Height = Height;

        var src = PresentationSource.FromVisual(this);
        if (src?.CompositionTarget != null)
        {
            _dpiX = src.CompositionTarget.TransformToDevice.M11;
            _dpiY = src.CompositionTarget.TransformToDevice.M22;
        }
    }

    // ── Phase 1 — Selection ────────────────────────────────────────────────

    private void Root_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_phase != Phase.Selecting) return;

        _selStart = e.GetPosition(Root);
        _isDragging = true;

        SelectionBorder.Visibility = Visibility.Visible;
        Canvas.SetLeft(SelectionBorder, _selStart.X);
        Canvas.SetTop(SelectionBorder, _selStart.Y);
        SelectionBorder.Width = 0;
        SelectionBorder.Height = 0;

        Root.CaptureMouse();
    }

    private void Root_MouseMove(object sender, MouseEventArgs e)
    {
        if (_phase != Phase.Selecting || !_isDragging) return;

        var cur = e.GetPosition(Root);
        var x = Math.Min(cur.X, _selStart.X);
        var y = Math.Min(cur.Y, _selStart.Y);
        var w = Math.Abs(cur.X - _selStart.X);
        var h = Math.Abs(cur.Y - _selStart.Y);

        Canvas.SetLeft(SelectionBorder, x);
        Canvas.SetTop(SelectionBorder, y);
        SelectionBorder.Width = w;
        SelectionBorder.Height = h;

        // Size label — pinned just above the top-left corner
        SizeLabelText.Text = $"{(int)(w * _dpiX)}×{(int)(h * _dpiY)}";
        SizeLabelBorder.Visibility = Visibility.Visible;
        SizeLabelBorder.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double ly = y - SizeLabelBorder.DesiredSize.Height - 4;
        if (ly < 0) ly = y + 4;
        Canvas.SetLeft(SizeLabelBorder, x);
        Canvas.SetTop(SizeLabelBorder, ly);
    }

    private void Root_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_phase != Phase.Selecting || !_isDragging) return;

        _isDragging = false;
        Root.ReleaseMouseCapture();

        var end = e.GetPosition(Root);
        var x = Math.Min(end.X, _selStart.X);
        var y = Math.Min(end.Y, _selStart.Y);
        var w = Math.Abs(end.X - _selStart.X);
        var h = Math.Abs(end.Y - _selStart.Y);

        if (w < 4 || h < 4)
        {
            Close();
            return;
        }

        _selection = new Rect(x, y, w, h);
        TransitionToAnnotating();
    }

    // ── Phase 2 — Annotating ───────────────────────────────────────────────

    private void TransitionToAnnotating()
    {
        _phase = Phase.Annotating;
        // Outer areas keep the cross cursor; annotation area gets its own
        Cursor = Cursors.Arrow;

        SizeLabelBorder.Visibility = Visibility.Collapsed;

        // Switch from solid dim to 4-strip dim so the selection is clear
        DimFull.Visibility = Visibility.Collapsed;
        LayoutDimStrips();

        // Size and position the annotation canvas exactly over the selection
        AnnotationCanvas.Width = _selection.Width;
        AnnotationCanvas.Height = _selection.Height;
        Canvas.SetLeft(AnnotationCanvas, _selection.X);
        Canvas.SetTop(AnnotationCanvas, _selection.Y);
        AnnotationCanvas.Visibility = Visibility.Visible;
        AnnotationCanvas.Cursor = Cursors.Cross;

        // Wire annotation mouse events
        AnnotationCanvas.MouseLeftButtonDown += Annot_Down;
        AnnotationCanvas.MouseMove           += Annot_Move;
        AnnotationCanvas.MouseLeftButtonUp   += Annot_Up;

        PositionToolbar();
        PositionActionBar();
    }

    private void LayoutDimStrips()
    {
        var s = _selection;
        var sw = Width;
        var sh = Height;

        DimTop.SetValue(Canvas.LeftProperty, 0d);
        DimTop.SetValue(Canvas.TopProperty, 0d);
        DimTop.Width = sw;
        DimTop.Height = s.Top;
        DimTop.Visibility = Visibility.Visible;

        DimBottom.SetValue(Canvas.LeftProperty, 0d);
        DimBottom.SetValue(Canvas.TopProperty, s.Bottom);
        DimBottom.Width = sw;
        DimBottom.Height = sh - s.Bottom;
        DimBottom.Visibility = Visibility.Visible;

        DimLeft.SetValue(Canvas.LeftProperty, 0d);
        DimLeft.SetValue(Canvas.TopProperty, s.Top);
        DimLeft.Width = s.Left;
        DimLeft.Height = s.Height;
        DimLeft.Visibility = Visibility.Visible;

        DimRight.SetValue(Canvas.LeftProperty, s.Right);
        DimRight.SetValue(Canvas.TopProperty, s.Top);
        DimRight.Width = sw - s.Right;
        DimRight.Height = s.Height;
        DimRight.Visibility = Visibility.Visible;
    }

    private void PositionToolbar()
    {
        AnnotToolbar.Visibility = Visibility.Visible;
        AnnotToolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var sz = AnnotToolbar.DesiredSize;

        double left = _selection.Right + 8;
        double top = _selection.Top;

        // If it bleeds off the right edge, put it on the left of the selection
        if (left + sz.Width > Width)
            left = _selection.Left - sz.Width - 8;

        top = Math.Max(0, Math.Min(top, Height - sz.Height));

        Canvas.SetLeft(AnnotToolbar, left);
        Canvas.SetTop(AnnotToolbar, top);
    }

    private void PositionActionBar()
    {
        ActionBar.Visibility = Visibility.Visible;
        ActionBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var sz = ActionBar.DesiredSize;

        // Centered horizontally below the selection
        double left = _selection.Left + (_selection.Width - sz.Width) / 2;
        double top = _selection.Bottom + 8;

        // If it bleeds off the bottom, flip above the selection
        if (top + sz.Height > Height)
            top = _selection.Top - sz.Height - 8;

        left = Math.Max(0, Math.Min(left, Width - sz.Width));
        top = Math.Max(0, top);

        Canvas.SetLeft(ActionBar, left);
        Canvas.SetTop(ActionBar, top);
    }

    // ── Annotation mouse handling ─────────────────────────────────────────

    private void Annot_Down(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(AnnotationCanvas);

        if (_tool == AnnotationTool.Text)
        {
            PlaceTextBox(p);
            return;
        }

        _annotStart = p;
        _annotDragging = true;
        AnnotationCanvas.CaptureMouse();
        BeginShape(p);
    }

    private void Annot_Move(object sender, MouseEventArgs e)
    {
        if (!_annotDragging) return;
        UpdateShape(e.GetPosition(AnnotationCanvas));
    }

    private void Annot_Up(object sender, MouseButtonEventArgs e)
    {
        if (!_annotDragging) return;
        _annotDragging = false;
        AnnotationCanvas.ReleaseMouseCapture();
        CommitShape(e.GetPosition(AnnotationCanvas));
    }

    // ── Shape drawing ─────────────────────────────────────────────────────

    private SolidColorBrush ActiveBrush() => new(_color);

    private void BeginShape(Point p)
    {
        switch (_tool)
        {
            case AnnotationTool.Arrow:
                _arrowShaft = new Line
                {
                    X1 = p.X, Y1 = p.Y, X2 = p.X, Y2 = p.Y, Stroke = ActiveBrush(), StrokeThickness = StrokeThick, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
                };
                _arrowHead = new Polyline
                {
                    Stroke = ActiveBrush(), StrokeThickness = StrokeThick, StrokeLineJoin = PenLineJoin.Round, StrokeEndLineCap = PenLineCap.Round
                };
                AnnotationCanvas.Children.Add(_arrowShaft);
                AnnotationCanvas.Children.Add(_arrowHead);
                break;

            case AnnotationTool.Line:
                _currentLine = new Line
                {
                    X1 = p.X, Y1 = p.Y, X2 = p.X, Y2 = p.Y, Stroke = ActiveBrush(), StrokeThickness = StrokeThick, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
                };
                AnnotationCanvas.Children.Add(_currentLine);
                break;

            case AnnotationTool.Highlight:
                _currentRect = new System.Windows.Shapes.Rectangle
                {
                    Fill = new SolidColorBrush(Color.FromArgb(100, _color.R, _color.G, _color.B))
                };
                Canvas.SetLeft(_currentRect, p.X);
                Canvas.SetTop(_currentRect, p.Y);
                AnnotationCanvas.Children.Add(_currentRect);
                break;

            case AnnotationTool.Pen:
                _currentPen = new Polyline
                {
                    Stroke = ActiveBrush(), StrokeThickness = StrokeThick, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
                };
                _currentPen.Points.Add(p);
                AnnotationCanvas.Children.Add(_currentPen);
                break;

            case AnnotationTool.Circle:
                _currentEllipse = new Ellipse
                {
                    Stroke = ActiveBrush(), StrokeThickness = StrokeThick, Fill = Brushes.Transparent
                };
                Canvas.SetLeft(_currentEllipse, p.X);
                Canvas.SetTop(_currentEllipse, p.Y);
                AnnotationCanvas.Children.Add(_currentEllipse);
                break;
        }
    }

    private void UpdateShape(Point p)
    {
        switch (_tool)
        {
            case AnnotationTool.Arrow when _arrowShaft != null && _arrowHead != null:
                _arrowShaft.X2 = p.X;
                _arrowShaft.Y2 = p.Y;
                RefreshArrowHead(_arrowShaft, _arrowHead);
                break;

            case AnnotationTool.Line when _currentLine != null:
                _currentLine.X2 = p.X;
                _currentLine.Y2 = p.Y;
                break;

            case AnnotationTool.Highlight when _currentRect != null:
                var rx = Math.Min(p.X, _annotStart.X);
                var ry = Math.Min(p.Y, _annotStart.Y);
                _currentRect.Width = Math.Abs(p.X - _annotStart.X);
                _currentRect.Height = Math.Abs(p.Y - _annotStart.Y);
                Canvas.SetLeft(_currentRect, rx);
                Canvas.SetTop(_currentRect, ry);
                break;

            case AnnotationTool.Pen when _currentPen != null:
                _currentPen.Points.Add(p);
                break;

            case AnnotationTool.Circle when _currentEllipse != null:
                var ex = Math.Min(p.X, _annotStart.X);
                var ey = Math.Min(p.Y, _annotStart.Y);
                _currentEllipse.Width = Math.Abs(p.X - _annotStart.X);
                _currentEllipse.Height = Math.Abs(p.Y - _annotStart.Y);
                Canvas.SetLeft(_currentEllipse, ex);
                Canvas.SetTop(_currentEllipse, ey);
                break;
        }
    }

    private void CommitShape(Point p)
    {
        UpdateShape(p);
        _arrowShaft = null; _arrowHead = null;
        _currentLine = null; _currentRect = null;
        _currentEllipse = null; _currentPen = null;
    }

    private static void RefreshArrowHead(Line shaft, Polyline head)
    {
        const double headLen = 12.0;
        const double headAngle = 25.0 * Math.PI / 180.0;

        var angle = Math.Atan2(shaft.Y2 - shaft.Y1, shaft.X2 - shaft.X1);

        head.Points.Clear();
        head.Points.Add(new Point(shaft.X2 - headLen * Math.Cos(angle + headAngle), shaft.Y2 - headLen * Math.Sin(angle + headAngle)));
        head.Points.Add(new Point(shaft.X2, shaft.Y2));
        head.Points.Add(new Point(shaft.X2 - headLen * Math.Cos(angle - headAngle), shaft.Y2 - headLen * Math.Sin(angle - headAngle)));
    }

    // ── Text tool ─────────────────────────────────────────────────────────

    private void PlaceTextBox(Point p)
    {
        var tb = new TextBox
        {
            Background = Brushes.Transparent, BorderBrush = new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)), BorderThickness = new Thickness(1), Foreground = ActiveBrush(), FontSize = 16, FontWeight = FontWeights.SemiBold, MinWidth = 60, AcceptsReturn = false, Padding = new Thickness(2)
        };
        Canvas.SetLeft(tb, p.X);
        Canvas.SetTop(tb, p.Y);
        AnnotationCanvas.Children.Add(tb);
        tb.Focus();

        tb.KeyDown   += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Escape)
            {
                FinalizeTextBox(tb);
                e.Handled = true;
            }
        };
        tb.LostFocus += (_, _) => FinalizeTextBox(tb);
    }

    private void FinalizeTextBox(TextBox tb)
    {
        if (!AnnotationCanvas.Children.Contains(tb)) return; // already removed

        if (string.IsNullOrWhiteSpace(tb.Text))
        {
            AnnotationCanvas.Children.Remove(tb);
            return;
        }

        var pos = new Point(Canvas.GetLeft(tb), Canvas.GetTop(tb));
        var text = tb.Text;
        var brush = tb.Foreground;

        AnnotationCanvas.Children.Remove(tb);

        // Replace editable box with a sealed TextBlock so it renders cleanly on copy
        var block = new TextBlock
        {
            Text = text, Foreground = brush, FontSize = 16, FontWeight = FontWeights.SemiBold
        };
        Canvas.SetLeft(block, pos.X);
        Canvas.SetTop(block, pos.Y);
        AnnotationCanvas.Children.Add(block);
    }

    // ── Tool & colour selection ───────────────────────────────────────────

    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton rb && rb.Tag is string tag)
        {
            _tool = Enum.Parse<AnnotationTool>(tag);
            AnnotationCanvas.Cursor = _tool == AnnotationTool.Text ? Cursors.IBeam : Cursors.Cross;
        }
    }

    private void ColorBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.ColorDialog
        {
            Color = System.Drawing.Color.FromArgb(_color.A, _color.R, _color.G, _color.B), FullOpen = true
        };

        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            var c = dlg.Color;
            _color = Color.FromArgb(c.A, c.R, c.G, c.B);
            ColorDot.Fill = new SolidColorBrush(_color);
        }
    }

    // ── Actions ───────────────────────────────────────────────────────────

    private void Copy_Click(object sender, RoutedEventArgs e)    => DoCopy();
    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void DoCopy()
    {
        // Physical pixel rect of the selection on screen
        var screenX = (int)((Left + _selection.X) * _dpiX);
        var screenY = (int)((Top  + _selection.Y) * _dpiY);
        var screenW = (int)(_selection.Width  * _dpiX);
        var screenH = (int)(_selection.Height * _dpiY);

        // Hide the overlay, wait for DWM repaint, then capture the underlying screen
        Visibility = Visibility.Hidden;
        System.Threading.Thread.Sleep(60);
        var screenBmp = ScreenCapture.Capture(screenX, screenY, screenW, screenH);
        Visibility = Visibility.Visible;

        // Render the annotation canvas at physical-pixel resolution
        var annotRtb = new RenderTargetBitmap(
            screenW, screenH, 96 * _dpiX, 96 * _dpiY, PixelFormats.Pbgra32);
        annotRtb.Render(AnnotationCanvas);

        // Composite: screen capture underneath, annotations on top
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var r = new Rect(0, 0, screenBmp.PixelWidth, screenBmp.PixelHeight);
            dc.DrawImage(screenBmp, r);
            dc.DrawImage(annotRtb, r);
        }

        var final = new RenderTargetBitmap(
            screenBmp.PixelWidth, screenBmp.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        final.Render(dv);

        System.Windows.Clipboard.SetImage(final);
        Close();
    }

    // ── Keyboard ──────────────────────────────────────────────────────────

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;
            case Key.C when e.KeyboardDevice.Modifiers == ModifierKeys.Control
                         && _phase == Phase.Annotating:
                DoCopy();
                break;
        }
    }
}