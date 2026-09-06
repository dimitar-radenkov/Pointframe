using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace Pointframe.Engine;

public sealed class RawFrameRecordingPipeline : IDisposable
{
    private readonly IRawFrameWriter _writer;
    private readonly RawFrameRecordingOptions _options;
    private readonly IRawFrameCapture _capture;
    private readonly ConcurrentQueue<byte[]> _bufferPool = new();
    private readonly SemaphoreSlim _pauseGate = new(1, 1);
    private readonly Channel<byte[]> _encodeChannel;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly Task _captureLoop;
    private readonly Task _encodeLoop;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly byte[] _latestFrameBytes;
    private bool _hasLatestFrame;
    private int _attemptedFrameCount;
    private int _writtenFrameCount;
    private int _droppedFrameCount;
    private long _firstFrameWrittenAtMilliseconds = -1;
    private bool _isPaused;
    private bool _isStopped;
    private bool _disposed;

    public RawFrameRecordingPipeline(IRawFrameWriter writer, RawFrameRecordingOptions options, IRawFrameCapture? capture = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.RedactionRegionsProvider);
        if (options.CaptureBoundsPixels.Width <= 0 || options.CaptureBoundsPixels.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (options.FramesPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (options.BufferPoolSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        _writer = writer;
        _options = options;
        _capture = capture ?? new GdiRawFrameCapture(options.CaptureBoundsPixels);
        var bufferSize = checked(options.CaptureBoundsPixels.Width * options.CaptureBoundsPixels.Height * 4);
        _latestFrameBytes = new byte[bufferSize];
        for (var index = 0; index < options.BufferPoolSize; index++)
        {
            _bufferPool.Enqueue(new byte[bufferSize]);
        }

        _encodeChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(options.BufferPoolSize)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
        });
        _captureLoop = Task.Run(CaptureLoopAsync);
        _encodeLoop = Task.Run(EncodeLoopAsync);
    }

    public bool IsPaused => _isPaused;

    public void Pause()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_isStopped || _isPaused)
        {
            return;
        }

        _pauseGate.Wait();
        _isPaused = true;
    }

    public void Resume()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_isStopped || !_isPaused)
        {
            return;
        }

        _isPaused = false;
        _pauseGate.Release();
    }

    public RawFrameRecordingStatistics Stop(TimeSpan targetElapsed)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_isStopped)
        {
            return GetStatistics();
        }

        _isStopped = true;
        if (_isPaused)
        {
            _isPaused = false;
            _pauseGate.Release();
        }

        _cancellation.Cancel();
        WaitForCompletion(_captureLoop, TimeSpan.FromSeconds(3));
        _encodeChannel.Writer.TryComplete();
        WaitForCompletion(_encodeLoop, TimeSpan.FromSeconds(10));
        PadToElapsedDuration(targetElapsed);
        return GetStatistics();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop(_stopwatch.Elapsed);
        _capture.Dispose();
        _cancellation.Dispose();
        _pauseGate.Dispose();
        _disposed = true;
    }

    public RawFrameRecordingStatistics GetStatistics()
    {
        var firstWriteMilliseconds = Volatile.Read(ref _firstFrameWrittenAtMilliseconds);
        return new RawFrameRecordingStatistics(
            Volatile.Read(ref _attemptedFrameCount),
            Volatile.Read(ref _writtenFrameCount),
            Volatile.Read(ref _droppedFrameCount),
            firstWriteMilliseconds < 0 ? null : TimeSpan.FromMilliseconds(firstWriteMilliseconds));
    }

    private async Task CaptureLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1d / _options.FramesPerSecond));
        while (await timer.WaitForNextTickAsync(_cancellation.Token).ConfigureAwait(false))
        {
            await _pauseGate.WaitAsync(_cancellation.Token).ConfigureAwait(false);
            _pauseGate.Release();
            CaptureFrameToChannel();
        }
    }

    private void CaptureFrameToChannel()
    {
        Interlocked.Increment(ref _attemptedFrameCount);
        if (!_bufferPool.TryDequeue(out var buffer))
        {
            Interlocked.Increment(ref _droppedFrameCount);
            return;
        }

        try
        {
            _capture.Capture(buffer);
            RawFramePixelation.Render(
                buffer,
                _options.CaptureBoundsPixels.Width,
                _options.CaptureBoundsPixels.Height,
                _options.RedactionRegionsProvider().Span);
            buffer.CopyTo(_latestFrameBytes, 0);
            _hasLatestFrame = true;
            if (_encodeChannel.Writer.TryWrite(buffer))
            {
                return;
            }

            Interlocked.Increment(ref _droppedFrameCount);
        }
        catch
        {
            _bufferPool.Enqueue(buffer);
            throw;
        }

        _bufferPool.Enqueue(buffer);
    }

    private async Task EncodeLoopAsync()
    {
        await foreach (var buffer in _encodeChannel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            _writer.WriteFrame(buffer);
            Interlocked.Increment(ref _writtenFrameCount);
            Interlocked.CompareExchange(
                ref _firstFrameWrittenAtMilliseconds,
                (long)_stopwatch.Elapsed.TotalMilliseconds,
                -1);
            _bufferPool.Enqueue(buffer);
        }
    }

    private void PadToElapsedDuration(TimeSpan targetElapsed)
    {
        var paddingSource = !_hasLatestFrame
            ? new byte[checked(_options.CaptureBoundsPixels.Width * _options.CaptureBoundsPixels.Height * 4)]
            : (byte[])_latestFrameBytes.Clone();
        var targetFrameCount = (int)Math.Ceiling(targetElapsed.TotalSeconds * _options.FramesPerSecond);
        var framesToPad = Math.Max(0, targetFrameCount - Volatile.Read(ref _writtenFrameCount));

        for (var index = 0; index < framesToPad; index++)
        {
            Interlocked.Increment(ref _attemptedFrameCount);
            _writer.WriteFrame(paddingSource);
            Interlocked.Increment(ref _writtenFrameCount);
        }
    }

    private static void WaitForCompletion(Task task, TimeSpan timeout)
    {
        try
        {
            task.Wait(timeout);
        }
        catch (AggregateException exception) when (exception.InnerExceptions.All(inner => inner is OperationCanceledException))
        {
        }
    }

    private sealed class GdiRawFrameCapture : IRawFrameCapture
    {
        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(
            IntPtr hdcDest, int xDest, int yDest, int width, int height,
            IntPtr hdcSrc, int xSrc, int ySrc, int rop);

        private const int SrcCopy = 0x00CC0020;

        private readonly PixelBounds _bounds;
        private readonly Bitmap _bitmap;
        private readonly Graphics _graphics;
        private readonly ScreenDc _screenDc;

        public GdiRawFrameCapture(PixelBounds bounds)
        {
            _bounds = bounds;
            _bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            _graphics = Graphics.FromImage(_bitmap);
            _screenDc = new ScreenDc();
        }

        public void Capture(byte[] frameData)
        {
            if (frameData.Length < checked(_bounds.Width * _bounds.Height * 4))
            {
                throw new ArgumentOutOfRangeException(nameof(frameData));
            }

            var bitmapDc = _graphics.GetHdc();
            try
            {
                BitBlt(bitmapDc, 0, 0, _bounds.Width, _bounds.Height, _screenDc.Handle, _bounds.X, _bounds.Y, SrcCopy);
            }
            finally
            {
                _graphics.ReleaseHdc(bitmapDc);
            }

            var bits = _bitmap.LockBits(new Rectangle(0, 0, _bounds.Width, _bounds.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                Marshal.Copy(bits.Scan0, frameData, 0, frameData.Length);
            }
            finally
            {
                _bitmap.UnlockBits(bits);
            }
        }

        public void Dispose()
        {
            _graphics.Dispose();
            _bitmap.Dispose();
            _screenDc.Dispose();
        }
    }

    private sealed class ScreenDc : IDisposable
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

        public IntPtr Handle { get; } = GetDC(IntPtr.Zero);

        public void Dispose()
        {
            ReleaseDC(IntPtr.Zero, Handle);
        }
    }
}

public static class RawFramePixelation
{
    private const int PixelBlockSize = 8;

    public static void Render(byte[] frameData, int frameWidth, int frameHeight, ReadOnlySpan<PixelBounds> regions)
    {
        ArgumentNullException.ThrowIfNull(frameData);
        if (frameWidth < 0 || frameHeight < 0 || frameData.Length < checked(frameWidth * frameHeight * 4))
        {
            throw new ArgumentOutOfRangeException(nameof(frameData));
        }

        foreach (var region in regions)
        {
            var left = Math.Clamp(region.X, 0, frameWidth);
            var top = Math.Clamp(region.Y, 0, frameHeight);
            var right = Math.Clamp((long)region.X + region.Width, 0, frameWidth);
            var bottom = Math.Clamp((long)region.Y + region.Height, 0, frameHeight);
            for (var blockTop = top; blockTop < bottom; blockTop += PixelBlockSize)
            {
                var blockBottom = Math.Min(blockTop + PixelBlockSize, (int)bottom);
                for (var blockLeft = left; blockLeft < right; blockLeft += PixelBlockSize)
                {
                    var blockRight = Math.Min(blockLeft + PixelBlockSize, (int)right);
                    var sourceOffset = ((blockTop * frameWidth) + blockLeft) * 4;
                    var blue = frameData[sourceOffset];
                    var green = frameData[sourceOffset + 1];
                    var red = frameData[sourceOffset + 2];
                    var alpha = frameData[sourceOffset + 3];
                    for (var y = blockTop; y < blockBottom; y++)
                    {
                        var offset = ((y * frameWidth) + blockLeft) * 4;
                        for (var x = blockLeft; x < blockRight; x++)
                        {
                            frameData[offset] = blue;
                            frameData[offset + 1] = green;
                            frameData[offset + 2] = red;
                            frameData[offset + 3] = alpha;
                            offset += 4;
                        }
                    }
                }
            }
        }
    }
}