using System.Buffers;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace Pointframe.Services;

// MediaCapture + MediaFrameReader in shared read-only mode, so a camera that Teams or Zoom already uses is never taken
// from them. Frames are converted to BGRA32 on the reader's worker thread, rate limited, and handed over one at a time.
internal sealed class WindowsCameraCaptureService : ICameraCaptureService
{
    private const uint TargetWidth = 640;
    private const long MinFrameIntervalMilliseconds = 33;
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    private readonly ILogger<WindowsCameraCaptureService> _logger;
    private readonly object _gate = new();

    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private bool _stopped;
    private int _processing;
    private long _lastFrameMilliseconds;

    public WindowsCameraCaptureService(ILogger<WindowsCameraCaptureService> logger)
    {
        _logger = logger;
    }

    public event Action<CameraFrame>? FrameArrived;

    public event Action<string>? Failed;

    public async Task<string?> StartAsync(string? deviceId)
    {
        MediaCapture? capture = null;
        var captureAdopted = false;
        try
        {
            capture = new MediaCapture();
            var settings = new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Video,
                SharingMode = MediaCaptureSharingMode.SharedReadOnly,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
            };
            if (!string.IsNullOrEmpty(deviceId))
            {
                settings.VideoDeviceId = deviceId;
            }

            await capture.InitializeAsync(settings).AsTask().ConfigureAwait(true);
            captureAdopted = true;
            if (!TryAdopt(capture))
            {
                return null;
            }

            capture.Failed += OnCaptureFailed;

            var source = PickColorSource(capture);
            if (source is null)
            {
                await StopAsync().ConfigureAwait(true);
                return CameraFailureReasons.NoDevice;
            }

            var reader = await CreateReaderAsync(capture, source).ConfigureAwait(true);
            reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            reader.FrameArrived += OnReaderFrameArrived;
            if (!TryAdopt(reader))
            {
                return null;
            }

            var status = await reader.StartAsync().AsTask().ConfigureAwait(true);
            if (status != MediaFrameReaderStartStatus.Success)
            {
                await StopAsync().ConfigureAwait(true);
                return status is MediaFrameReaderStartStatus.ExclusiveControlNotAvailable or MediaFrameReaderStartStatus.DeviceNotAvailable
                    ? CameraFailureReasons.Busy
                    : CameraFailureReasons.Other;
            }

            return null;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Starting the camera failed");
            if (!captureAdopted)
            {
                capture?.Dispose();
            }

            var reason = CameraFailureReasons.FromException(exception);
            await StopAsync().ConfigureAwait(true);
            return reason;
        }
    }

    public async Task StopAsync()
    {
        MediaFrameReader? reader;
        MediaCapture? capture;
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            reader = _reader;
            capture = _capture;
            _reader = null;
            _capture = null;
        }

        try
        {
            if (reader is not null)
            {
                reader.FrameArrived -= OnReaderFrameArrived;
                await reader.StopAsync().AsTask().WaitAsync(StopTimeout).ConfigureAwait(true);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Stopping the camera frame reader failed");
        }
        finally
        {
            try
            {
                reader?.Dispose();
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "Disposing the camera frame reader failed");
            }
        }

        try
        {
            if (capture is not null)
            {
                capture.Failed -= OnCaptureFailed;
                capture.Dispose();
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Releasing the camera failed");
        }
    }

    private bool TryAdopt(MediaCapture capture)
    {
        lock (_gate)
        {
            if (!_stopped)
            {
                _capture = capture;
                return true;
            }
        }

        capture.Dispose();
        return false;
    }

    private bool TryAdopt(MediaFrameReader reader)
    {
        lock (_gate)
        {
            if (!_stopped)
            {
                _reader = reader;
                return true;
            }
        }

        reader.FrameArrived -= OnReaderFrameArrived;
        reader.Dispose();
        return false;
    }

    private static MediaFrameSource? PickColorSource(MediaCapture capture)
    {
        var colorSources = capture.FrameSources.Values
            .Where(source => source.Info.SourceKind == MediaFrameSourceKind.Color)
            .ToList();
        return colorSources.FirstOrDefault(source => source.Info.MediaStreamType == MediaStreamType.VideoPreview)
            ?? colorSources.FirstOrDefault(source => source.Info.MediaStreamType == MediaStreamType.VideoRecord)
            ?? colorSources.FirstOrDefault();
    }

    private async Task<MediaFrameReader> CreateReaderAsync(MediaCapture capture, MediaFrameSource source)
    {
        try
        {
            var format = source.CurrentFormat?.VideoFormat;
            if (format is { Width: > 0, Height: > 0 })
            {
                var width = Math.Min(format.Width, TargetWidth);
                var height = (uint)Math.Max(2d, Math.Round((double)width * format.Height / format.Width));
                var size = new BitmapSize { Width = width - (width % 2), Height = height - (height % 2) };
                return await capture
                    .CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8, size)
                    .AsTask()
                    .ConfigureAwait(true);
            }
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "The camera reader could not scale the frames; converting without scaling");
        }

        try
        {
            return await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8).AsTask().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "The camera reader could not convert to BGRA; reading the native format");
        }

        return await capture.CreateFrameReaderAsync(source).AsTask().ConfigureAwait(true);
    }

    private void OnCaptureFailed(MediaCapture sender, MediaCaptureFailedEventArgs errorEventArgs)
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }
        }

        _logger.LogWarning("Camera capture failed: code={Code}", errorEventArgs.Code);
        Failed?.Invoke(CameraFailureReasons.Lost);
    }

    private void OnReaderFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        if (Interlocked.CompareExchange(ref _processing, 1, 0) != 0)
        {
            return;
        }

        try
        {
            lock (_gate)
            {
                if (_stopped)
                {
                    return;
                }
            }

            using var mediaFrame = sender.TryAcquireLatestFrame();
            var now = Environment.TickCount64;
            if (mediaFrame is null || now - _lastFrameMilliseconds < MinFrameIntervalMilliseconds)
            {
                return;
            }

            using var bitmap = mediaFrame.VideoMediaFrame?.SoftwareBitmap;
            if (bitmap is null)
            {
                return;
            }

            var frame = ConvertToFrame(bitmap);
            _lastFrameMilliseconds = now;
            Publish(frame);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "A camera frame could not be processed");
        }
        finally
        {
            Volatile.Write(ref _processing, 0);
        }
    }

    private void Publish(CameraFrame frame)
    {
        var handler = FrameArrived;
        if (handler is null)
        {
            frame.Dispose();
            return;
        }

        try
        {
            handler(frame);
        }
        catch
        {
            frame.Dispose();
            throw;
        }
    }

    private static CameraFrame ConvertToFrame(SoftwareBitmap bitmap)
    {
        SoftwareBitmap? converted = null;
        try
        {
            var source = bitmap;
            if (source.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
            {
                converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
                source = converted;
            }

            var width = source.PixelWidth;
            var height = source.PixelHeight;
            var step = CameraFrameConverter.GetDecimationStep(width);
            var length = width * height * 4;
            if (step == 1)
            {
                var frame = CameraFrame.Rent(width, height);
                try
                {
                    source.CopyToBuffer(frame.Pixels.AsBuffer(0, frame.Length));
                    return frame;
                }
                catch
                {
                    frame.Dispose();
                    throw;
                }
            }

            var scratch = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                source.CopyToBuffer(scratch.AsBuffer(0, length));
                return CameraFrameConverter.CopyDecimated(scratch.AsSpan(0, length), width, height, width * 4, step);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(scratch);
            }
        }
        finally
        {
            converted?.Dispose();
        }
    }
}
