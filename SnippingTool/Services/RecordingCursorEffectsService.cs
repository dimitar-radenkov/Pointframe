using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using SnippingTool.Models;

namespace SnippingTool.Services;

internal sealed class RecordingCursorEffectsService : IDisposable
{
    private const double DefaultCursorHighlightDiameter = 28d;
    private const double MinCursorHighlightDiameter = 8d;
    private const double MaxCursorHighlightDiameter = 96d;
    private const double ClickRippleStartDiameter = 16d;
    private const double ClickRippleEndScale = 3d;
    private const double ClickRippleStrokeThickness = 3d;
    private static readonly TimeSpan CursorPollingInterval = TimeSpan.FromMilliseconds(16);
    private static readonly Duration ClickRippleAnimationDuration = new(TimeSpan.FromMilliseconds(320));

    private readonly Canvas _canvas;
    private readonly RecordingSessionGeometry _geometry;
    private readonly IMouseHookService _mouseHookService;
    private readonly IUserSettingsService _userSettingsService;
    private readonly Func<bool> _isAnnotationInputArmed;
    private readonly ILogger<RecordingCursorEffectsService> _logger;
    private readonly DispatcherTimer _cursorTimer;
    private readonly Ellipse _cursorHighlightRing;
    private readonly Func<Point?> _getCursorScreenPoint;
    private Point? _lastCursorScreenPoint;
    private bool _isStarted;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    public RecordingCursorEffectsService(
        Canvas canvas,
        RecordingSessionGeometry geometry,
        IMouseHookService mouseHookService,
        IUserSettingsService userSettingsService,
        Func<bool> isAnnotationInputArmed,
        ILogger<RecordingCursorEffectsService> logger,
        Func<Point?>? getCursorScreenPoint = null)
    {
        _canvas = canvas;
        _geometry = geometry;
        _mouseHookService = mouseHookService;
        _userSettingsService = userSettingsService;
        _isAnnotationInputArmed = isAnnotationInputArmed;
        _logger = logger;
        _getCursorScreenPoint = getCursorScreenPoint ?? GetCursorScreenPoint;
        _cursorHighlightRing = CreateCursorHighlightRing();
        _cursorTimer = new DispatcherTimer(DispatcherPriority.Render, _canvas.Dispatcher)
        {
            Interval = CursorPollingInterval,
        };
        _cursorTimer.Tick += HandleCursorTimerTick;
    }

    public void Start()
    {
        if (_isStarted)
        {
            return;
        }

        _canvas.Width = _geometry.HostBoundsDips.Width;
        _canvas.Height = _geometry.HostBoundsDips.Height;
        Canvas.SetLeft(_canvas, 0d);
        Canvas.SetTop(_canvas, 0d);
        _canvas.Children.Clear();
        _canvas.Children.Add(_cursorHighlightRing);
        HideCursorHighlight();
        _mouseHookService.MouseButtonDown += HandleMouseButtonDown;
        _mouseHookService.Start();
        _cursorTimer.Start();
        _isStarted = true;

        _logger.LogDebug(
            "Recording cursor effects layer initialized: hostDipWidth={HostDipWidth}, hostDipHeight={HostDipHeight}",
            _geometry.HostBoundsDips.Width,
            _geometry.HostBoundsDips.Height);
    }

    public void Dispose()
    {
        _cursorTimer.Stop();
        _cursorTimer.Tick -= HandleCursorTimerTick;
        _mouseHookService.MouseButtonDown -= HandleMouseButtonDown;
        _mouseHookService.Stop();
        _lastCursorScreenPoint = null;
        _isStarted = false;
        _canvas.Children.Clear();
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    private static Ellipse CreateCursorHighlightRing()
    {
        return new Ellipse
        {
            Width = DefaultCursorHighlightDiameter,
            Height = DefaultCursorHighlightDiameter,
            StrokeThickness = 3d,
            Stroke = new SolidColorBrush(Color.FromArgb(220, 255, 210, 64)),
            Fill = new SolidColorBrush(Color.FromArgb(48, 255, 210, 64)),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
    }

    internal void UpdateCursorHighlight()
    {
        var cursorScreenPoint = _getCursorScreenPoint();
        if (cursorScreenPoint is null)
        {
            HideCursorHighlight();
            return;
        }

        if (_lastCursorScreenPoint == cursorScreenPoint.Value)
        {
            return;
        }

        _lastCursorScreenPoint = cursorScreenPoint.Value;

        if (!_userSettingsService.Current.RecordingCursorHighlightEnabled
            || !_geometry.IsScreenPixelPointInsideCapture(cursorScreenPoint.Value))
        {
            HideCursorHighlight();
            return;
        }

        var cursorHighlightDiameter = ClampCursorHighlightDiameter(_userSettingsService.Current.RecordingCursorHighlightSize);
        _cursorHighlightRing.Width = cursorHighlightDiameter;
        _cursorHighlightRing.Height = cursorHighlightDiameter;

        var hostPoint = _geometry.MapScreenPixelPointToHostDips(cursorScreenPoint.Value);
        Canvas.SetLeft(_cursorHighlightRing, hostPoint.X - (cursorHighlightDiameter / 2d));
        Canvas.SetTop(_cursorHighlightRing, hostPoint.Y - (cursorHighlightDiameter / 2d));
        _cursorHighlightRing.Visibility = Visibility.Visible;
    }

    private void HandleCursorTimerTick(object? sender, EventArgs e)
    {
        UpdateCursorHighlight();
    }

    private void HandleMouseButtonDown(object? sender, MouseHookEventArgs e)
    {
        if (e.Button != MouseHookButton.Left)
        {
            return;
        }

        if (_isAnnotationInputArmed()
            || !_userSettingsService.Current.RecordingClickRippleEnabled)
        {
            return;
        }

        if (!_geometry.IsScreenPixelPointInsideCapture(e.ScreenPoint))
        {
            return;
        }

        var hostPoint = _geometry.MapScreenPixelPointToHostDips(e.ScreenPoint);
        ShowClickRipple(hostPoint);
    }

    private void HideCursorHighlight()
    {
        _cursorHighlightRing.Visibility = Visibility.Collapsed;
    }

    private void ShowClickRipple(Point hostPoint)
    {
        var ripple = new Ellipse
        {
            Width = ClickRippleStartDiameter,
            Height = ClickRippleStartDiameter,
            StrokeThickness = ClickRippleStrokeThickness,
            Stroke = new SolidColorBrush(Color.FromArgb(220, 255, 210, 64)),
            Fill = Brushes.Transparent,
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1d, 1d),
            Opacity = 0.85d,
        };

        Canvas.SetLeft(ripple, hostPoint.X - (ClickRippleStartDiameter / 2d));
        Canvas.SetTop(ripple, hostPoint.Y - (ClickRippleStartDiameter / 2d));
        _canvas.Children.Add(ripple);

        var storyboard = new Storyboard();
        var opacityAnimation = new DoubleAnimation(0.85d, 0d, ClickRippleAnimationDuration);
        var scaleXAnimation = new DoubleAnimation(1d, ClickRippleEndScale, ClickRippleAnimationDuration);
        var scaleYAnimation = new DoubleAnimation(1d, ClickRippleEndScale, ClickRippleAnimationDuration);

        Storyboard.SetTarget(opacityAnimation, ripple);
        Storyboard.SetTargetProperty(opacityAnimation, new PropertyPath(UIElement.OpacityProperty));
        Storyboard.SetTarget(scaleXAnimation, ripple);
        Storyboard.SetTargetProperty(scaleXAnimation, new PropertyPath("RenderTransform.(ScaleTransform.ScaleX)"));
        Storyboard.SetTarget(scaleYAnimation, ripple);
        Storyboard.SetTargetProperty(scaleYAnimation, new PropertyPath("RenderTransform.(ScaleTransform.ScaleY)"));

        storyboard.Children.Add(opacityAnimation);
        storyboard.Children.Add(scaleXAnimation);
        storyboard.Children.Add(scaleYAnimation);
        storyboard.Completed += (_, _) => _canvas.Children.Remove(ripple);
        storyboard.Begin();
    }

    private static Point? GetCursorScreenPoint()
    {
        if (GetCursorPos(out var nativePoint))
        {
            return new Point(nativePoint.X, nativePoint.Y);
        }

        return null;
    }

    private static double ClampCursorHighlightDiameter(double size)
    {
        return Math.Clamp(size, MinCursorHighlightDiameter, MaxCursorHighlightDiameter);
    }
}
