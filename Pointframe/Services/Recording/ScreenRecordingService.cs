using System.Diagnostics;
using System.Windows;
using Pointframe.Engine;

namespace Pointframe.Services;

public sealed class ScreenRecordingService : IScreenRecordingService
{
    private readonly ILogger<ScreenRecordingService> _logger;
    private readonly IMicrophoneDeviceService _microphoneDeviceService;
    private readonly IUserSettingsService _settings;
    private readonly IVideoWriterFactory _writerFactory;
    private IVideoWriter? _writer;
    private RawFrameRecordingPipeline? _pipeline;
    private int _fps;
    private RecordingMicrophoneSession? _microphoneSession;
    private Stopwatch? _sessionStopwatch;
    private IRecordingEventTrack? _eventTrack;
    private IRecordingRedactionSession? _redactionSession;

    public bool IsRecording { get; private set; }
    public bool IsPaused { get; private set; }
    public bool IsRecordingMicrophoneEnabled { get; private set; }
    public bool CanToggleMicrophone { get; private set; }
    public bool IsMicrophoneMuted { get; private set; }
    public RecordingEventTrackSummary? EventTrackSummary { get; private set; }

    public ScreenRecordingService(ILogger<ScreenRecordingService> logger, IMicrophoneDeviceService microphoneDeviceService, IUserSettingsService settings, IVideoWriterFactory writerFactory)
    {
        _logger = logger;
        _microphoneDeviceService = microphoneDeviceService;
        _settings = settings;
        _writerFactory = writerFactory;
    }

    public void Start(int x, int y, int width, int height, string outputPath)
    {
        var fps = _settings.Current.RecordingFps;
        _logger.LogInformation("Recording Start requested: region=({X},{Y},{W},{H}), fps={Fps}, path={Path}", x, y, width, height, fps, outputPath);
        if (IsRecording)
        {
            _logger.LogWarning("Start called while already recording - ignored");
            return;
        }

        width -= width % 2;
        height -= height % 2;
        if (width <= 0 || height <= 0)
        {
            _logger.LogError("Region too small after even-dimension truncation: {W}x{H} - aborting", width, height);
            return;
        }

        _fps = fps;
        _sessionStopwatch = Stopwatch.StartNew();
        EventTrackSummary = null;
        try
        {
            var microphoneDeviceName = ResolveMicrophoneDeviceName();
            _writer = _writerFactory.Create(width, height, fps, outputPath, microphoneDeviceName);
            _microphoneSession = new RecordingMicrophoneSession(_microphoneDeviceService, _logger, microphoneDeviceName);
            IsRecordingMicrophoneEnabled = _microphoneSession.IsEnabled;
            CanToggleMicrophone = _microphoneSession.CanToggleMute;
            IsMicrophoneMuted = _microphoneSession.InitialMutedState;
            _eventTrack = new RecordingEventTrack(outputPath, _sessionStopwatch);
            _redactionSession = new RecordingRedactionSession(_eventTrack);
            _eventTrack.Write("recording.started", new RecordingEventPayload(CaptureX: x, CaptureY: y, CaptureWidth: width, CaptureHeight: height, FramesPerSecond: fps, IsEnabled: IsRecordingMicrophoneEnabled, IsMuted: IsMicrophoneMuted));
            _pipeline = new RawFrameRecordingPipeline(
                new VideoWriterRawFrameWriter(_writer),
                new RawFrameRecordingOptions(new PixelBounds(x, y, width, height), fps, () => _redactionSession?.SnapshotPixelBounds() ?? ReadOnlyMemory<PixelBounds>.Empty));
            IsRecording = true;
            _logger.LogInformation("Recording started: {W}x{H} @ {Fps}fps (MP4) to {Path}", width, height, fps, outputPath);
        }
        catch
        {
            IsRecording = false;
            IsPaused = false;
            ResetMicrophoneFlags();
            _pipeline?.Dispose();
            _pipeline = null;
            _writer?.Dispose();
            _writer = null;
            _microphoneSession = null;
            CompleteEventTrack();
            ReleaseSessionReferences();
            throw;
        }
    }

    public void Stop()
    {
        if (!IsRecording)
        {
            _logger.LogDebug("Stop called while not recording - ignored");
            return;
        }

        _logger.LogInformation("Stopping recording");
        IsRecording = false;
        _eventTrack?.Write("recording.stopped", new RecordingEventPayload());
        var elapsed = _sessionStopwatch?.Elapsed ?? TimeSpan.Zero;
        RawFrameRecordingStatistics? statistics = null;
        try
        {
            statistics = _pipeline?.Stop(elapsed);
        }
        finally
        {
            LogSessionSummary(elapsed, statistics ?? _pipeline?.GetStatistics());
            _pipeline?.Dispose();
            _pipeline = null;
            ResetMicrophoneFlags();
            try
            {
                _writer?.Dispose();
                _logger.LogInformation("Writer closed - file finalised");
            }
            finally
            {
                _microphoneSession?.RestoreInitialMuteState();
                _microphoneSession = null;
                _writer = null;
                CompleteEventTrack();
                ReleaseSessionReferences();
            }
        }
    }

    public void Pause()
    {
        if (!IsRecording || IsPaused)
        {
            return;
        }

        _pipeline?.Pause();
        IsPaused = true;
        _eventTrack?.Write("recording.paused", new RecordingEventPayload());
        _logger.LogInformation("Recording paused");
    }

    public void Resume()
    {
        if (!IsRecording || !IsPaused)
        {
            return;
        }

        _pipeline?.Resume();
        IsPaused = false;
        _eventTrack?.Write("recording.resumed", new RecordingEventPayload());
        _logger.LogInformation("Recording resumed");
    }

    public bool TrySetMicrophoneMuted(bool isMuted)
    {
        if (!CanToggleMicrophone || _microphoneSession is null || !_microphoneSession.TrySetMuted(isMuted))
        {
            return false;
        }

        IsMicrophoneMuted = isMuted;
        _eventTrack?.Write("microphone.changed", new RecordingEventPayload(IsEnabled: IsRecordingMicrophoneEnabled, IsMuted: isMuted));
        _logger.LogInformation("Recording microphone {State}", isMuted ? "muted" : "unmuted");
        return true;
    }

    public bool TryAddRedaction(Int32Rect captureLocalBounds)
    {
        if (!IsRecording || _redactionSession is null)
        {
            return false;
        }

        _redactionSession.Add(captureLocalBounds);
        return true;
    }

    public bool ClearRedactions()
    {
        return IsRecording && _redactionSession?.Clear() == true;
    }

    public void Dispose()
    {
        Stop();
    }

    private void ResetMicrophoneFlags()
    {
        IsRecordingMicrophoneEnabled = false;
        CanToggleMicrophone = false;
        IsMicrophoneMuted = false;
    }

    private void ReleaseSessionReferences()
    {
        _sessionStopwatch = null;
        _eventTrack = null;
        _redactionSession = null;
        _fps = 0;
        IsPaused = false;
    }

    private string? ResolveMicrophoneDeviceName()
    {
        if (!_settings.Current.RecordMicrophone)
        {
            _logger.LogInformation("Microphone recording is disabled in settings. Continuing with video only.");
            return null;
        }

        var microphoneDeviceName = _settings.Current.RecordingMicrophoneDeviceName;
        if (string.IsNullOrWhiteSpace(microphoneDeviceName))
        {
            microphoneDeviceName = _microphoneDeviceService.GetDefaultCaptureDeviceName();
        }

        var availableDevices = _microphoneDeviceService.GetAvailableCaptureDeviceNames();
        if (!string.IsNullOrWhiteSpace(microphoneDeviceName) && availableDevices.Any(device => string.Equals(device, microphoneDeviceName, StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogInformation("Microphone recording enabled using capture device '{DeviceName}'", microphoneDeviceName);
            return microphoneDeviceName;
        }

        microphoneDeviceName = _microphoneDeviceService.GetDefaultCaptureDeviceName();
        if (!string.IsNullOrWhiteSpace(microphoneDeviceName) && availableDevices.Any(device => string.Equals(device, microphoneDeviceName, StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogInformation("Configured microphone device was unavailable; falling back to default capture device '{DeviceName}'", microphoneDeviceName);
            return microphoneDeviceName;
        }

        _logger.LogWarning("Microphone recording is enabled, but no compatible capture device name could be resolved. Continuing with video only.");
        return null;
    }

    private void CompleteEventTrack()
    {
        if (_eventTrack is not null)
        {
            EventTrackSummary = _eventTrack.Complete();
        }
    }

    private void LogSessionSummary(TimeSpan elapsed, RawFrameRecordingStatistics? statistics)
    {
        if (_fps <= 0 || statistics is null)
        {
            return;
        }

        var outputDuration = TimeSpan.FromSeconds((double)statistics.WrittenFrameCount / _fps);
        var droppedDuration = TimeSpan.FromSeconds((double)statistics.DroppedFrameCount / _fps);
        _logger.LogInformation("Recording session stats: elapsed={ElapsedMs} ms, attemptedFrames={AttemptedFrames}, writtenFrames={WrittenFrames}, droppedFrames={DroppedFrames}, firstWriteDelayMs={FirstWriteDelayMs}, outputDuration={OutputDuration:c}, droppedDuration={DroppedDuration:c}", (long)elapsed.TotalMilliseconds, statistics.AttemptedFrameCount, statistics.WrittenFrameCount, statistics.DroppedFrameCount, statistics.FirstFrameWriteDelay?.TotalMilliseconds ?? -1, outputDuration, droppedDuration);
    }

    private sealed class VideoWriterRawFrameWriter(IVideoWriter writer) : IRawFrameWriter
    {
        public void WriteFrame(byte[] frameData)
        {
            writer.WriteFrame(frameData);
        }
    }
}
