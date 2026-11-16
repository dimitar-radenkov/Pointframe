using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Pointframe.Services;

// Records whatever the default Windows playback device plays (WASAPI loopback) into a temporary WAV.
public sealed class WasapiSystemAudioCaptureService : ISystemAudioCaptureService
{
    private const int SilenceChunkBytes = 64 * 1024;

    private readonly ILogger<WasapiSystemAudioCaptureService> _logger;
    private readonly object _gate = new();
    private WasapiRecorder? _capture;
    private WaveFileWriter? _writer;
    private SystemAudioTimeline? _timeline;
    private string? _wavPath;
    private string? _failureReason;
    private bool _stopping;

    public WasapiSystemAudioCaptureService(ILogger<WasapiSystemAudioCaptureService> logger)
    {
        _logger = logger;
    }

    public bool IsCapturing { get; private set; }

    public event Action<string>? CaptureFailed;

    public bool TryStart(string wavPath, out string? failureReason)
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
                _capture = new WasapiRecorderBuilder().WithLoopbackCapture().Build();
                var format = _capture.WaveFormat;
                _writer = new WaveFileWriter(wavPath, format);
                _wavPath = wavPath;
                _failureReason = null;
                _stopping = false;
                var stopwatch = Stopwatch.StartNew();
                _timeline = new SystemAudioTimeline(format.AverageBytesPerSecond, format.BlockAlign, () => stopwatch.Elapsed);
                _capture.DataAvailable += OnDataAvailable;
                _capture.RecordingStopped += OnRecordingStopped;
                _capture.StartRecording();
                IsCapturing = true;
                _logger.LogInformation("System audio loopback capture started ({SampleRate} Hz, {Channels} ch)", format.SampleRate, format.Channels);
                return true;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "System audio loopback capture could not start");
                failureReason = "start_failed";
                ReleaseResources(deleteFile: true);
                return false;
            }
        }
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

    public SystemAudioStopResult Stop()
    {
        WasapiRecorder? capture;
        lock (_gate)
        {
            if (_capture is null)
            {
                return new SystemAudioStopResult(null, _failureReason);
            }

            _stopping = true;
            capture = _capture;
        }

        try
        {
            capture.StopRecording();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "System audio loopback capture did not stop cleanly");
        }

        lock (_gate)
        {
            var wavPath = _wavPath;
            var failure = _failureReason;
            if (failure is null && _writer is not null && _timeline is not null)
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

            ReleaseResources(deleteFile: failure is not null);
            return failure is null ? new SystemAudioStopResult(wavPath, null) : new SystemAudioStopResult(null, failure);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_capture is not null)
            {
                _stopping = true;
                try
                {
                    _capture.StopRecording();
                }
                catch (Exception exception)
                {
                    _logger.LogDebug(exception, "System audio loopback capture stop during dispose failed");
                }
            }

            ReleaseResources(deleteFile: true);
        }
    }

    private void OnDataAvailable(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        string? failure = null;
        lock (_gate)
        {
            if (_writer is null || _timeline is null || _failureReason is not null)
            {
                return;
            }

            try
            {
                var (silence, write) = _timeline.PlanBlock(buffer.Length);
                if (silence > 0)
                {
                    WriteSilence(silence);
                }

                if (write)
                {
                    _writer.Write(buffer);
                    _timeline.RecordWritten(buffer.Length);
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

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        string? failure = null;
        lock (_gate)
        {
            if (_stopping || e.Exception is null || _failureReason is not null)
            {
                return;
            }

            _logger.LogWarning(e.Exception, "System audio loopback capture stopped unexpectedly");
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
        if (_writer is null || _timeline is null)
        {
            return;
        }

        var chunk = new byte[(int)Math.Min(bytes, SilenceChunkBytes)];
        while (bytes > 0)
        {
            var count = (int)Math.Min(bytes, chunk.Length);
            _writer.Write(chunk, 0, count);
            _timeline.RecordWritten(count);
            bytes -= count;
        }
    }

    private void ReleaseResources(bool deleteFile)
    {
        IsCapturing = false;
        var path = _wavPath;
        if (_capture is not null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _capture.Dispose();
            _capture = null;
        }

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
