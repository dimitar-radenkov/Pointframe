using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace SnippingTool.Services;

public sealed class ScreenRecordingService : IScreenRecordingService
{
    private readonly ILogger<ScreenRecordingService> _logger;
    private readonly IUserSettingsService _settings;
    private readonly IVideoWriterFactory _writerFactory;
    private IVideoWriter? _writer;
    private CancellationTokenSource? _cts;
    private Task? _captureLoop;
    private byte[]? _buffer;
    private int _captureX;
    private int _captureY;
    private int _captureWidth;
    private int _captureHeight;
    private int _fps;
    private readonly SemaphoreSlim _pauseGate = new SemaphoreSlim(1, 1);

    public bool IsRecording { get; private set; }
    public bool IsPaused { get; private set; }

    public ScreenRecordingService(
        ILogger<ScreenRecordingService> logger,
        IUserSettingsService settings,
        IVideoWriterFactory writerFactory)
    {
        _logger = logger;
        _settings = settings;
        _writerFactory = writerFactory;
    }

    public void Start(
        int x,
        int y,
        int width,
        int height,
        string outputPath)
    {
        var fps = _settings.Current.RecordingFps;
        _logger.LogInformation("Recording Start requested: region=({X},{Y},{W},{H}), fps={Fps}, path={Path}",
            x, y, width, height, fps, outputPath);

        if (IsRecording)
        {
            _logger.LogWarning("Start called while already recording — ignored");
            return;
        }

        // JPEG MCU requires even dimensions
        width = width % 2 == 0 ? width : width - 1;
        height = height % 2 == 0 ? height : height - 1;
        if (width <= 0 || height <= 0)
        {
            _logger.LogError("Region too small after even-dimension truncation: {W}x{H} — aborting", width, height);
            return;
        }

        _captureX = x;
        _captureY = y;
        _captureWidth = width;
        _captureHeight = height;
        _fps = fps;
        _buffer = new byte[width * height * 4];

        var format = _settings.Current.RecordingFormat;
        _writer = _writerFactory.Create(format, width, height, fps, outputPath);

        _cts = new CancellationTokenSource();
        IsRecording = true;
        _captureLoop = Task.Run(() => CaptureLoopAsync(_cts.Token));
        _logger.LogInformation("Recording started: {W}x{H} @ {Fps}fps ({Format}) → {Path}", width, height, fps, format, outputPath);
    }

    public void Stop()
    {
        if (!IsRecording)
        {
            _logger.LogDebug("Stop called while not recording — ignored");
            return;
        }

        _logger.LogInformation("Stopping recording");
        IsRecording = false;
        if (IsPaused)
        {
            IsPaused = false;
            _pauseGate.Release();
        }

        _cts?.Cancel();
        try
        {
            _captureLoop?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException ae) when (ae.InnerExceptions.All(ex => ex is OperationCanceledException))
        {
        }
        finally
        {
            _writer?.Dispose();
            _writer = null;
            _logger.LogInformation("Writer closed — file finalised");
            _cts?.Dispose();
            _cts = null;
            _captureLoop = null;
        }
    }

    private async Task CaptureLoopAsync(CancellationToken ct)
    {
        _logger.LogDebug("Capture loop started");
        var interval = TimeSpan.FromMilliseconds(1000.0 / _fps);
        using var timer = new PeriodicTimer(interval);
        var frameCount = 0;

        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await _pauseGate.WaitAsync(ct).ConfigureAwait(false);
                _pauseGate.Release();
                CaptureFrame();
                frameCount++;
                if (frameCount % 100 == 0)
                {
                    _logger.LogDebug("Captured {FrameCount} frames so far", frameCount);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Capture loop failed after {FrameCount} frames", frameCount);
            throw;
        }

        _logger.LogDebug("Capture loop ended after {FrameCount} frames", frameCount);
    }

    private void CaptureFrame()
    {
        if (_writer is null || _buffer is null)
        {
            return;
        }

        using var bmp = new Bitmap(_captureWidth, _captureHeight, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(_captureX, _captureY, 0, 0, bmp.Size, CopyPixelOperation.SourceCopy);

        var data = bmp.LockBits(
            new Rectangle(0, 0, _captureWidth, _captureHeight),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            Marshal.Copy(data.Scan0, _buffer, 0, _buffer.Length);
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        _writer.WriteFrame(_buffer);
    }

    public void Pause()
    {
        if (!IsRecording || IsPaused)
        {
            return;
        }

        _pauseGate.Wait();
        IsPaused = true;
        _logger.LogInformation("Recording paused");
    }

    public void Resume()
    {
        if (!IsRecording || !IsPaused)
        {
            return;
        }

        IsPaused = false;
        _pauseGate.Release();
        _logger.LogInformation("Recording resumed");
    }

    public void Dispose() => Stop();
}
