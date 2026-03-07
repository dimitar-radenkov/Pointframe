using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;

namespace SnippingTool.Services;

public sealed class FFMpegVideoWriter : IVideoWriter
{
    private readonly Process _ffmpeg;
    private readonly Stream _stdin;
    private readonly ILogger _logger;
    private bool _closed;

    public FFMpegVideoWriter(
        int width,
        int height,
        int fps,
        string outputPath,
        ILogger logger)
    {
        _logger = logger;

        var ffmpegPath = ResolveFfmpegPath();
        if (!File.Exists(ffmpegPath))
        {
            throw new FileNotFoundException(
                "ffmpeg.exe not found. MP4 recording requires ffmpeg. " +
                "Please place ffmpeg.exe in the application directory or select AVI format in Settings.",
                ffmpegPath);
        }

        var args = string.Join(" ",
            "-y",
            "-f rawvideo",
            "-pix_fmt bgra",
            $"-s {width}x{height}",
            $"-r {fps}",
            "-i pipe:0",
            "-c:v libx264",
            "-preset ultrafast",
            "-crf 23",
            "-pix_fmt yuv420p",
            $"\"{outputPath}\"");

        _ffmpeg = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
            }
        };

        _ffmpeg.Start();
        _stdin = _ffmpeg.StandardInput.BaseStream;
        _ = ConsumeStderrAsync(_ffmpeg);
        _logger.LogInformation("FFMpeg process started (PID {Pid}): {Args}", _ffmpeg.Id, args);
    }

    public void WriteFrame(byte[] frameData) => _stdin.Write(frameData, 0, frameData.Length);

    public void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _stdin.Close();

        if (!_ffmpeg.WaitForExit(TimeSpan.FromSeconds(10)))
        {
            _logger.LogWarning("ffmpeg did not exit within 10 s — killing");
            _ffmpeg.Kill();
        }

        if (_ffmpeg.ExitCode != 0)
        {
            _logger.LogError("ffmpeg exited with code {Code}", _ffmpeg.ExitCode);
        }
        else
        {
            _logger.LogInformation("ffmpeg exited cleanly");
        }

        _ffmpeg.Dispose();
    }

    public void Dispose() => Close();

    private async Task ConsumeStderrAsync(Process process)
    {
        try
        {
            var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                _logger.LogDebug("ffmpeg stderr: {Stderr}", stderr);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read ffmpeg stderr");
        }
    }

    private static string ResolveFfmpegPath()
    {
        // 1. Look next to the application binary
        var appDir = AppContext.BaseDirectory;
        var candidate = Path.Combine(appDir, "ffmpeg.exe");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        // 2. Look in Assets/ffmpeg subfolder
        candidate = Path.Combine(appDir, "Assets", "ffmpeg", "ffmpeg.exe");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        // 3. Fall back to PATH-resolved ffmpeg
        return "ffmpeg.exe";
    }
}
