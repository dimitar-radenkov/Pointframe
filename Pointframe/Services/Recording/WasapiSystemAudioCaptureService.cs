using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Pointframe.Services;

internal delegate void LoopbackPacketHandler(ReadOnlySpan<byte> data, bool silent, bool timestampValid, TimeSpan captureTime);

internal interface ILoopbackSource : IDisposable
{
    WaveFormat WaveFormat { get; }
    event LoopbackPacketHandler? PacketAvailable;
    event Action<Exception?>? Stopped;
    void Start();
    void Stop();
}

internal sealed class WasapiLoopbackSource : ILoopbackSource
{
    private readonly WasapiRecorder _recorder = new WasapiRecorderBuilder().WithLoopbackCapture().Build();

    public WasapiLoopbackSource()
    {
        _recorder.DataAvailable += OnDataAvailable;
        _recorder.RecordingStopped += OnRecordingStopped;
    }

    public WaveFormat WaveFormat => _recorder.WaveFormat;

    public event LoopbackPacketHandler? PacketAvailable;

    public event Action<Exception?>? Stopped;

    public void Start() => _recorder.StartRecording();

    public void Stop() => _recorder.StopRecording();

    public void Dispose()
    {
        _recorder.DataAvailable -= OnDataAvailable;
        _recorder.RecordingStopped -= OnRecordingStopped;
        _recorder.Dispose();
    }

    private void OnDataAvailable(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        // qpcPosition is the QueryPerformanceCounter value in 100 ns units, the same base as WasapiSystemAudioCaptureService.QpcNow.
        PacketAvailable?.Invoke(buffer, (flags & AudioClientBufferFlags.Silent) != 0, (flags & AudioClientBufferFlags.TimestampError) == 0, TimeSpan.FromTicks(qpcPosition));
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        Stopped?.Invoke(e.Exception);
    }
}

// Records whatever the default Windows playback device plays (WASAPI loopback) into a temporary WAV.
public sealed class WasapiSystemAudioCaptureService : ISystemAudioCaptureService
{
    private const int SilenceChunkBytes = 64 * 1024;

    private readonly ILogger<WasapiSystemAudioCaptureService> _logger;
    private readonly Func<ILoopbackSource> _sourceFactory;
    private readonly Func<TimeSpan> _clock;
    private readonly object _gate = new();
    private readonly object _lifecycle = new();
    private ILoopbackSource? _source;
    private WaveFileWriter? _writer;
    private SystemAudioTimeline? _timeline;
    private WaveFormat? _format;
    private string? _wavPath;
    private string? _failureReason;
    private bool _stopping;

    public WasapiSystemAudioCaptureService(ILogger<WasapiSystemAudioCaptureService> logger)
        : this(logger, () => new WasapiLoopbackSource(), QpcNow)
    {
    }

    internal WasapiSystemAudioCaptureService(ILogger<WasapiSystemAudioCaptureService> logger, Func<ILoopbackSource> sourceFactory, Func<TimeSpan> clock)
    {
        _logger = logger;
        _sourceFactory = sourceFactory;
        _clock = clock;
    }

    public bool IsCapturing { get; private set; }

    public event Action<string>? CaptureFailed;

    internal static TimeSpan QpcNow() => TimeSpan.FromTicks((long)(Stopwatch.GetTimestamp() * (10_000_000d / Stopwatch.Frequency)));

    // Long recordings overflow a plain RIFF file at 4 GB (about 3 hours of 48 kHz stereo float); RF64 lifts that limit.
    internal static WaveFileWriterOptions CreateWriterOptions() => new() { EnableRf64 = true };

    public bool TryStart(string wavPath, out string? failureReason)
    {
        failureReason = null;
        lock (_lifecycle)
        {
            return TryStartCore(wavPath, out failureReason);
        }
    }

    // Starts the loopback device. The timeline origin is set separately by BeginTimeline, once the video starts,
    // so a slow device initialization cannot shift the audio against the video.
    public void BeginTimeline()
    {
        lock (_gate)
        {
            if (_source is null || _timeline is not null || _format is null)
            {
                return;
            }

            _timeline = new SystemAudioTimeline(_format.AverageBytesPerSecond, _format.BlockAlign, _clock, _clock());
        }
    }

    private bool TryStartCore(string wavPath, out string? failureReason)
    {
        failureReason = null;
        lock (_gate)
        {
            if (IsCapturing)
            {
                return true;
            }

            try
            {
                _source = _sourceFactory();
                var format = _source.WaveFormat;
                _writer = new WaveFileWriter(wavPath, format, CreateWriterOptions());
                _wavPath = wavPath;
                _failureReason = null;
                _stopping = false;
                _format = format;
                _timeline = null;
                _source.PacketAvailable += OnPacket;
                _source.Stopped += OnStopped;
                _source.Start();
                IsCapturing = true;
                _logger.LogInformation("System audio loopback capture started ({SampleRate} Hz, {Channels} ch)", format.SampleRate, format.Channels);
                return true;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "System audio loopback capture could not start");
                failureReason = "start_failed";
            }
        }

        ShutdownCore(discard: true);
        return false;
    }

    public void Pause()
    {
        lock (_gate)
        {
            _timeline?.Pause();
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            _timeline?.Resume();
        }
    }

    public SystemAudioStopResult Stop() => Shutdown(discard: false);

    public void Dispose() => Shutdown(discard: true);

    // The capture thread is joined by StopRecording/Dispose and its callbacks take _gate, so neither may run while _gate is held.
    private SystemAudioStopResult Shutdown(bool discard)
    {
        lock (_lifecycle)
        {
            return ShutdownCore(discard);
        }
    }

    // Callers hold _lifecycle, so Stop, Dispose and TryStart never interleave; the callbacks only take _gate.
    private SystemAudioStopResult ShutdownCore(bool discard)
    {
        ILoopbackSource? source;
        lock (_gate)
        {
            _stopping = true;
            IsCapturing = false;
            source = _source;
            _source = null;
        }

        if (source is not null)
        {
            source.PacketAvailable -= OnPacket;
            source.Stopped -= OnStopped;
            try
            {
                source.Stop();
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "System audio loopback capture did not stop cleanly");
            }

            try
            {
                source.Dispose();
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "System audio loopback capture dispose failed");
            }
        }

        lock (_gate)
        {
            var wavPath = _wavPath;
            var failure = _failureReason;
            if (!discard && failure is null && _writer is not null && _timeline is not null)
            {
                try
                {
                    WriteSilence(_timeline.SilenceBytesToFinish());
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "System audio could not be padded to the recording length");
                    failure = "write_failed";
                }
            }

            CloseWriter(deleteFile: discard || failure is not null);
            if (discard)
            {
                return new SystemAudioStopResult(null, failure);
            }

            return failure is null ? new SystemAudioStopResult(wavPath, null) : new SystemAudioStopResult(null, failure);
        }
    }

    private void OnPacket(ReadOnlySpan<byte> data, bool silent, bool timestampValid, TimeSpan captureTime)
    {
        string? failure = null;
        lock (_gate)
        {
            if (_stopping || _writer is null || _timeline is null || _failureReason is not null)
            {
                return;
            }

            try
            {
                foreach (var plan in _timeline.PlanPacket(captureTime, data.Length, timestampValid))
                {
                    WriteSilence(plan.SilenceBytesBefore);
                    var segment = plan.Segment;
                    if (silent)
                    {
                        WriteSilence(segment.Length);
                    }
                    else
                    {
                        _writer.Write(data.Slice(segment.Offset, segment.Length));
                    }
                }
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "System audio could not be written");
                _failureReason = "write_failed";
                failure = _failureReason;
            }
        }

        if (failure is not null)
        {
            RaiseFailed(failure);
        }
    }

    private void OnStopped(Exception? exception)
    {
        string? failure = null;
        lock (_gate)
        {
            if (_stopping || exception is null || _failureReason is not null)
            {
                return;
            }

            _logger.LogWarning(exception, "System audio loopback capture stopped unexpectedly");
            _failureReason = "device_lost";
            failure = _failureReason;
        }

        RaiseFailed(failure);
    }

    private void RaiseFailed(string reason)
    {
        try
        {
            CaptureFailed?.Invoke(reason);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "System audio failure handler threw");
        }
    }

    private void WriteSilence(long bytes)
    {
        if (_writer is null || bytes <= 0)
        {
            return;
        }

        var chunk = new byte[(int)Math.Min(bytes, SilenceChunkBytes)];
        while (bytes > 0)
        {
            var count = (int)Math.Min(bytes, chunk.Length);
            _writer.Write(chunk, 0, count);
            bytes -= count;
        }
    }

    private void CloseWriter(bool deleteFile)
    {
        var path = _wavPath;
        try
        {
            _writer?.Dispose();
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "System audio WAV writer dispose failed");
        }

        _writer = null;
        _timeline = null;
        _wavPath = null;
        if (deleteFile && path is not null)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(exception, "Could not delete the system audio WAV {Path}", path);
            }
        }
    }
}
