namespace Pointframe.Services;

// Owns the optional camera for one recording. It never throws into the recording: every failure hides the bubble, is
// reported once through ICameraNoticeService, and leaves the recording untouched.
//
// Threading: StartAsync, SetOnAsync and Dispose are called on the UI thread. Frames, failures and the stall timer arrive
// on worker threads; all shared state is guarded by _gate and events are raised outside the lock. The camera service is
// created per start and stopped exactly once (toggle off, failure, or Dispose). A late start result for a stopped or
// disposed session is ignored.
public sealed class RecordingCameraSession : IRecordingCameraSession
{
    internal static readonly TimeSpan DefaultStartTimeout = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromSeconds(2);
    private const int StallCheckIntervalMilliseconds = 500;

    private readonly Func<ICameraCaptureService> _serviceFactory;
    private readonly ICameraDeviceService _devices;
    private readonly IUserSettingsService _settings;
    private readonly ICameraNoticeService _notice;
    private readonly ILogger _logger;
    private readonly object _gate = new();

    private ICameraCaptureService? _service;
    private CameraFrame? _pending;
    private System.Threading.Timer? _stallTimer;
    private bool _disposed;
    private bool _busy;
    private bool _isOn;
    private bool _bubbleVisible;
    private bool _frameSignalled;
    private bool _startCompleted;
    private bool _everHadFrame;
    private bool _stallReported;
    private long _lastFrameMilliseconds;
    private int _generation;
    private string? _deviceIdSnapshot;
    private bool _deviceIdSnapshotTaken;

    public RecordingCameraSession(
        Func<ICameraCaptureService> serviceFactory,
        ICameraDeviceService devices,
        IUserSettingsService settings,
        ICameraNoticeService notice,
        ILogger<RecordingCameraSession> logger)
    {
        _serviceFactory = serviceFactory;
        _devices = devices;
        _settings = settings;
        _notice = notice;
        _logger = logger;
    }

    internal TimeSpan StartTimeout { get; set; } = DefaultStartTimeout;

    internal TimeSpan StallTimeout { get; set; } = DefaultStallTimeout;

    internal Func<long> NowMilliseconds { get; set; } = () => Environment.TickCount64;

    internal bool EnableStallTimer { get; set; } = true;

    public event Action? FrameAvailable;

    public event Action? BubbleVisibilityChanged;

    public event Action? StateChanged;

    public bool IsOn
    {
        get
        {
            lock (_gate)
            {
                return _isOn;
            }
        }
    }

    public bool IsBubbleVisible
    {
        get
        {
            lock (_gate)
            {
                return _bubbleVisible;
            }
        }
    }

    // Starts the camera and waits at most StartTimeout. A camera that is late keeps starting in the background and its
    // bubble appears when frames arrive; the caller (the recording) is never held longer than the bounded wait.
    public async Task StartAsync()
    {
        if (!_settings.Current.RecordCamera)
        {
            return;
        }

        await StartCameraAsync().ConfigureAwait(true);
    }

    public async Task SetOnAsync(bool on)
    {
        if (on)
        {
            await StartCameraAsync().ConfigureAwait(true);
        }
        else
        {
            await StopCurrentAsync().ConfigureAwait(true);
        }
    }

    public CameraFrame? TryTakeFrame()
    {
        lock (_gate)
        {
            var frame = _pending;
            _pending = null;
            _frameSignalled = false;
            return frame;
        }
    }

    public void Dispose()
    {
        ICameraCaptureService? service;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _generation++;
            _busy = false;
            _isOn = false;
            _bubbleVisible = false;
            service = _service;
            _service = null;
            DisposePendingFrame();
            DisposeTimer();
        }

        if (service is not null)
        {
            _ = StopQuietlyAsync(service);
        }
    }

    internal void CheckStall()
    {
        string? phase = null;
        var hide = false;
        lock (_gate)
        {
            if (_disposed || _service is null || !_startCompleted)
            {
                return;
            }

            if (NowMilliseconds() - _lastFrameMilliseconds < StallTimeout.TotalMilliseconds)
            {
                return;
            }

            if (_bubbleVisible)
            {
                _bubbleVisible = false;
                hide = true;
            }

            if (!_stallReported)
            {
                _stallReported = true;
                phase = _everHadFrame ? CameraFailureReasons.PhaseCapture : CameraFailureReasons.PhaseStart;
            }
        }

        if (hide)
        {
            Raise(BubbleVisibilityChanged);
        }

        if (phase is not null)
        {
            _logger.LogWarning("Camera frames stalled; hiding the bubble");
            _notice.ReportUnavailable(CameraFailureReasons.Stalled, phase);
        }
    }

    private async Task StartCameraAsync()
    {
        int generation;
        lock (_gate)
        {
            if (_disposed || _busy || _service is not null)
            {
                return;
            }

            _busy = true;
            generation = ++_generation;
            _isOn = true;
            _startCompleted = false;
            _everHadFrame = false;
            _stallReported = false;
        }

        Raise(StateChanged);

        var startTask = RunStartAsync(generation);
        await Task.WhenAny(startTask, Task.Delay(StartTimeout)).ConfigureAwait(true);
    }

    // Never throws: every outcome is handled in CompleteStartAsync.
    private async Task RunStartAsync(int generation)
    {
        ICameraCaptureService? service = null;
        try
        {
            var settings = _settings.Current;
            if (!_deviceIdSnapshotTaken)
            {
                _deviceIdSnapshot = string.IsNullOrWhiteSpace(settings.CameraDeviceId) ? null : settings.CameraDeviceId;
                _deviceIdSnapshotTaken = true;
            }

            var deviceId = _deviceIdSnapshot;
            string? failure = null;

            try
            {
                var devices = await _devices.GetCamerasAsync().ConfigureAwait(true);
                if (devices.Count == 0)
                {
                    failure = CameraFailureReasons.NoDevice;
                }
                else if (deviceId is not null && !devices.Any(device => string.Equals(device.Id, deviceId, StringComparison.Ordinal)))
                {
                    // A saved camera that is gone is never silently replaced by another one.
                    failure = CameraFailureReasons.DeviceMissing;
                }
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Camera enumeration failed; trying to start the camera anyway");
            }

            if (failure is null)
            {
                lock (_gate)
                {
                    if (_disposed || generation != _generation)
                    {
                        return;
                    }

                    service = _serviceFactory();
                    _service = service;
                }

                var started = service;
                started.FrameArrived += frame => OnFrame(started, frame);
                started.Failed += reason => _ = OnServiceFailedAsync(started, reason);
                failure = await started.StartAsync(deviceId).ConfigureAwait(true);
            }

            await CompleteStartAsync(generation, service, failure).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Camera start failed");
            await CompleteStartAsync(generation, service, CameraFailureReasons.FromException(exception)).ConfigureAwait(true);
        }
    }

    private async Task CompleteStartAsync(int generation, ICameraCaptureService? service, string? failure)
    {
        if (failure is null)
        {
            lock (_gate)
            {
                if (_disposed || generation != _generation)
                {
                    // Stopped or disposed while starting: that path already stopped the service.
                    return;
                }

                _busy = false;
                _startCompleted = true;
                _lastFrameMilliseconds = NowMilliseconds();
                if (EnableStallTimer)
                {
                    _stallTimer ??= new System.Threading.Timer(_ => CheckStall(), null, StallCheckIntervalMilliseconds, StallCheckIntervalMilliseconds);
                }
            }

            return;
        }

        await FailAsync(generation, service, failure, CameraFailureReasons.PhaseStart).ConfigureAwait(true);
    }

    private Task OnServiceFailedAsync(ICameraCaptureService service, string reason)
    {
        int generation;
        string phase;
        lock (_gate)
        {
            generation = _generation;
            phase = _startCompleted ? CameraFailureReasons.PhaseCapture : CameraFailureReasons.PhaseStart;
        }

        return FailAsync(generation, service, CameraFailureReasons.Normalize(reason), phase);
    }

    private async Task FailAsync(int generation, ICameraCaptureService? service, string reason, string phase)
    {
        ICameraCaptureService? toStop = null;
        bool wasVisible;
        lock (_gate)
        {
            if (_disposed || generation != _generation || (service is not null && !ReferenceEquals(service, _service)))
            {
                return;
            }

            toStop = service;
            _service = null;
            _generation++;
            _busy = false;
            _isOn = false;
            wasVisible = _bubbleVisible;
            _bubbleVisible = false;
            DisposePendingFrame();
            DisposeTimer();
        }

        if (wasVisible)
        {
            Raise(BubbleVisibilityChanged);
        }

        Raise(StateChanged);

        _logger.LogWarning("Camera unavailable: reason={Reason} phase={Phase}", reason, phase);
        _notice.ReportUnavailable(reason, phase);

        if (toStop is not null)
        {
            await StopQuietlyAsync(toStop).ConfigureAwait(true);
        }
    }

    private async Task StopCurrentAsync()
    {
        ICameraCaptureService? service;
        bool wasVisible;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            service = _service;
            _service = null;
            _generation++;
            _busy = false;
            _isOn = false;
            wasVisible = _bubbleVisible;
            _bubbleVisible = false;
            DisposePendingFrame();
            DisposeTimer();
        }

        if (wasVisible)
        {
            Raise(BubbleVisibilityChanged);
        }

        Raise(StateChanged);

        if (service is not null)
        {
            await StopQuietlyAsync(service).ConfigureAwait(true);
        }

    }

    private void OnFrame(ICameraCaptureService service, CameraFrame frame)
    {
        var signalFrame = false;
        var showBubble = false;
        var accepted = false;
        lock (_gate)
        {
            if (!_disposed && ReferenceEquals(service, _service))
            {
                accepted = true;
                _lastFrameMilliseconds = NowMilliseconds();
                _everHadFrame = true;
                _pending?.Dispose();
                _pending = frame;
                if (!_frameSignalled)
                {
                    _frameSignalled = true;
                    signalFrame = true;
                }

                if (!_bubbleVisible)
                {
                    _bubbleVisible = true;
                    showBubble = true;
                }
            }
        }

        if (!accepted)
        {
            frame.Dispose();
            return;
        }

        if (signalFrame)
        {
            Raise(FrameAvailable);
        }

        if (showBubble)
        {
            Raise(BubbleVisibilityChanged);
        }
    }

    private async Task StopQuietlyAsync(ICameraCaptureService service)
    {
        try
        {
            await service.StopAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Stopping the camera failed");
        }
    }

    private void DisposePendingFrame()
    {
        _pending?.Dispose();
        _pending = null;
        _frameSignalled = false;
    }

    private void DisposeTimer()
    {
        _stallTimer?.Dispose();
        _stallTimer = null;
    }

    private void Raise(Action? handler)
    {
        try
        {
            handler?.Invoke();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "A camera session event handler failed");
        }
    }
}
