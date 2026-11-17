using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Pointframe.Services;

// Places the camera bubble in the recording overlay and paints camera frames into one reusable WriteableBitmap.
// The bubble is a plain element of the overlay root (never a child of the annotation canvas, so undo and clear cannot
// remove it) and is not hit-test visible, so input falls through it to the annotation canvas and the passthrough hit
// regions (HUD, capture surface) never need to know about it. Everything here runs on the overlay's dispatcher; session
// events arrive on worker threads and are marshalled with a single queued update.
internal sealed class RecordingCameraBubbleCoordinator : IDisposable
{
    private readonly FrameworkElement _bubble;
    private readonly ImageBrush _brush;
    private readonly RecordingSessionGeometry _geometry;
    private readonly IUserSettingsService _userSettings;
    private readonly Func<Rect?> _getHudRectDips;
    private readonly IRecordingCameraSession _session;
    private readonly Dispatcher _dispatcher;

    private WriteableBitmap? _bitmap;
    private bool _disposed;

    public RecordingCameraBubbleCoordinator(
        FrameworkElement bubble,
        ImageBrush brush,
        RecordingSessionGeometry geometry,
        IUserSettingsService userSettings,
        Func<Rect?> getHudRectDips,
        IRecordingCameraSession session)
    {
        _bubble = bubble;
        _brush = brush;
        _geometry = geometry;
        _userSettings = userSettings;
        _getHudRectDips = getHudRectDips;
        _session = session;
        _dispatcher = bubble.Dispatcher;

        _bubble.IsHitTestVisible = false;
        _session.FrameAvailable += HandleFrameAvailable;
        _session.BubbleVisibilityChanged += HandleBubbleVisibilityChanged;

        // A frame that arrived before this subscription already consumed the session's one signal; drain it now.
        Post(ApplyPendingFrame);
        Post(UpdateLayout);
    }

    internal Rect? CurrentHostRectDips { get; private set; }

    internal bool HasBitmap => _bitmap is not null;

    public void UpdateLayout()
    {
        if (_disposed)
        {
            return;
        }

        Rect? rect = null;
        if (_session.IsBubbleVisible && _bitmap is not null)
        {
            var settings = _userSettings.Current;
            rect = CameraBubblePlacement.Calculate(_geometry, settings.CameraBubbleSize, settings.CameraBubbleCorner, _getHudRectDips());
        }

        CurrentHostRectDips = rect;
        if (rect is not { } placed)
        {
            _bubble.Visibility = Visibility.Collapsed;
            return;
        }

        Canvas.SetLeft(_bubble, placed.Left);
        Canvas.SetTop(_bubble, placed.Top);
        _bubble.Width = placed.Width;
        _bubble.Height = placed.Height;
        _bubble.Visibility = Visibility.Visible;
    }

    public void ApplyPendingFrame()
    {
        using var frame = _session.TryTakeFrame();
        if (_disposed || frame is null)
        {
            return;
        }

        var isFirstBitmap = _bitmap is null;
        if (_bitmap is null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
        {
            _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96d, 96d, PixelFormats.Bgr32, null);
            _brush.ImageSource = _bitmap;
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Pixels, frame.Stride, 0);
        if (isFirstBitmap)
        {
            UpdateLayout();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session.FrameAvailable -= HandleFrameAvailable;
        _session.BubbleVisibilityChanged -= HandleBubbleVisibilityChanged;
        _bubble.Visibility = Visibility.Collapsed;
        _brush.ImageSource = null;
        _bitmap = null;
        CurrentHostRectDips = null;

        // Hand a frame that is still waiting back to the pool.
        _session.TryTakeFrame()?.Dispose();
    }

    private void HandleFrameAvailable()
    {
        Post(ApplyPendingFrame);
    }

    private void HandleBubbleVisibilityChanged()
    {
        Post(UpdateLayout);
    }

    private void Post(Action action)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
            {
                if (!_disposed)
                {
                    action();
                }
            });
        }
        catch (Exception)
        {
            // The dispatcher is shutting down with the window; late camera callbacks are ignored.
        }
    }
}
