using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharpAvi.Codecs;
using SharpAvi.Output;
using SnippingTool.Models;

namespace SnippingTool.Services;

public sealed class ScreenRecordingService : IScreenRecordingService
{
    private readonly ILogger<ScreenRecordingService> _logger;
    private readonly IOptions<RecordingOptions> _options;
    private AviWriter? _writer;
    private IAviVideoStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _captureLoop;
    private byte[]? _buffer;
    private int _captureX;
    private int _captureY;
    private int _captureWidth;
    private int _captureHeight;
    private int _fps;

    public bool IsRecording { get; private set; }

    public ScreenRecordingService(ILogger<ScreenRecordingService> logger, IOptions<RecordingOptions> options)
    {
        _logger = logger;
        _options = options;
    }

    public void Start(int x, int y, int width, int height, string outputPath)
    {
        var fps = _options.Value.Fps;
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

        _writer = new AviWriter(outputPath) { FramesPerSecond = fps, EmitIndex1 = true };
        var encoder = new MotionJpegVideoEncoderWpf(width, height, _options.Value.JpegQuality);
        _stream = _writer.AddEncodingVideoStream(encoder, ownsEncoder: true, width, height);

        _cts = new CancellationTokenSource();
        IsRecording = true;
        _captureLoop = Task.Run(() => CaptureLoopAsync(_cts.Token));
        _logger.LogInformation("Recording started: {W}x{H} @ {Fps}fps → {Path}", width, height, fps, outputPath);
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
        _cts?.Cancel();
        try
        {
            _captureLoop?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
            // Capture loop was cancelled — expected
        }
        finally
        {
            _writer?.Close();
            _logger.LogInformation("AVI writer closed — file finalised");
            _writer = null;
            _stream = null;
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
        if (_stream is null || _buffer is null)
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

        _stream.WriteFrame(true, _buffer, 0, _buffer.Length);
    }

    public void Dispose() => Stop();
}
