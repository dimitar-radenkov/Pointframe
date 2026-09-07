using System.ComponentModel;
using System.Diagnostics;

namespace Pointframe.Engine;

public sealed class FfmpegDirectVideoWriterFactory : IDirectVideoWriterFactory
{
    public IDirectVideoWriter Create(int width, int height, int framesPerSecond, string outputPath)
    {
        return new FfmpegDirectVideoWriter(width, height, framesPerSecond, outputPath);
    }
}

public sealed class FfmpegDirectVideoWriter : IDirectVideoWriter
{
    private readonly Process _process;
    private readonly Stream _input;
    private bool _disposed;

    public FfmpegDirectVideoWriter(int width, int height, int framesPerSecond, string outputPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveFfmpegPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("rawvideo");
        startInfo.ArgumentList.Add("-pix_fmt");
        startInfo.ArgumentList.Add("bgra");
        startInfo.ArgumentList.Add("-s");
        startInfo.ArgumentList.Add($"{width}x{height}");
        startInfo.ArgumentList.Add("-r");
        startInfo.ArgumentList.Add(framesPerSecond.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add("pipe:0");
        startInfo.ArgumentList.Add("-c:v");
        startInfo.ArgumentList.Add("libx264");
        startInfo.ArgumentList.Add("-preset");
        startInfo.ArgumentList.Add("ultrafast");
        startInfo.ArgumentList.Add("-crf");
        startInfo.ArgumentList.Add("23");
        startInfo.ArgumentList.Add("-pix_fmt");
        startInfo.ArgumentList.Add("yuv420p");
        startInfo.ArgumentList.Add("-g");
        startInfo.ArgumentList.Add(framesPerSecond.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(outputPath);

        _process = new Process { StartInfo = startInfo };
        try
        {
            _process.Start();
        }
        catch (Win32Exception exception)
        {
            throw new FileNotFoundException("ffmpeg.exe was not found. Add it to PATH or set POINTFRAME_FFMPEG_PATH.", startInfo.FileName, exception);
        }

        _input = _process.StandardInput.BaseStream;
        _ = _process.StandardError.ReadToEndAsync();
    }

    public void WriteFrame(byte[] frameData)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _input.Write(frameData, 0, frameData.Length);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _input.Dispose();
        if (!_process.WaitForExit(10000))
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit();
        }

        var exitCode = _process.ExitCode;
        _process.Dispose();
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg exited with code {exitCode} while finalizing the direct recording.");
        }
    }

    private static string ResolveFfmpegPath()
    {
        var configuredPath = Environment.GetEnvironmentVariable("POINTFRAME_FFMPEG_PATH");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        var bundledPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        return File.Exists(bundledPath) ? bundledPath : "ffmpeg.exe";
    }
}