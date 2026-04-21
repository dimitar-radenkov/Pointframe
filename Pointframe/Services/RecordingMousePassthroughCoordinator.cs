using System.Windows.Threading;

namespace Pointframe.Services;

internal sealed class RecordingMousePassthroughCoordinator : IDisposable
{
    private static readonly TimeSpan CursorPollingInterval = TimeSpan.FromMilliseconds(16);

    private readonly Func<bool> _isAnnotationInputArmed;
    private readonly Func<System.Windows.Point?> _getCursorScreenPoint;
    private readonly Func<System.Windows.Point, bool> _isPointInsideRecordingHud;
    private readonly Action<bool> _setWindowMouseTransparency;
    private readonly DispatcherTimer _cursorTimer;

    private bool _isDisposed;
    private bool _isWindowMouseTransparent;

    public RecordingMousePassthroughCoordinator(
        Func<bool> isAnnotationInputArmed,
        Func<System.Windows.Point?> getCursorScreenPoint,
        Func<System.Windows.Point, bool> isPointInsideRecordingHud,
        Action<bool> setWindowMouseTransparency,
        Dispatcher dispatcher)
    {
        _isAnnotationInputArmed = isAnnotationInputArmed;
        _getCursorScreenPoint = getCursorScreenPoint;
        _isPointInsideRecordingHud = isPointInsideRecordingHud;
        _setWindowMouseTransparency = setWindowMouseTransparency;
        _cursorTimer = new DispatcherTimer(DispatcherPriority.Input, dispatcher)
        {
            Interval = CursorPollingInterval,
        };
        _cursorTimer.Tick += HandleCursorTimerTick;
    }

    public void Start()
    {
        if (_isDisposed)
        {
            return;
        }

        _cursorTimer.Start();
        Update();
    }

    public void Update()
    {
        if (_isDisposed)
        {
            return;
        }

        var shouldBeTransparent = ShouldWindowBeMouseTransparent();
        if (shouldBeTransparent == _isWindowMouseTransparent)
        {
            return;
        }

        _setWindowMouseTransparency(shouldBeTransparent);
        _isWindowMouseTransparent = shouldBeTransparent;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _cursorTimer.Stop();
        _cursorTimer.Tick -= HandleCursorTimerTick;
        if (_isWindowMouseTransparent)
        {
            _setWindowMouseTransparency(false);
            _isWindowMouseTransparent = false;
        }

        _isDisposed = true;
    }

    private void HandleCursorTimerTick(object? sender, EventArgs e)
    {
        Update();
    }

    private bool ShouldWindowBeMouseTransparent()
    {
        if (_isAnnotationInputArmed())
        {
            return false;
        }

        var cursorScreenPoint = _getCursorScreenPoint();
        if (cursorScreenPoint is null)
        {
            return true;
        }

        return !_isPointInsideRecordingHud(cursorScreenPoint.Value);
    }
}
