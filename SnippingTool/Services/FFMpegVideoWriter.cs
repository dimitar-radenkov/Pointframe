using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;

namespace Pointframe.Services;

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
        ILogger logger,
        string? microphoneDeviceName = null)
    {
        _logger = logger;

        var ffmpegPath = FfmpegResolver.ResolveRequired("Screen recording");

        var args = BuildArguments(width, height, fps, outputPath, microphoneDeviceName);

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

        try
        {
            _ffmpeg.Start();
        }
        catch (Win32Exception ex) when (string.Equals(ffmpegPath, "ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw FfmpegResolver.CreateMissingException("Screen recording", ffmpegPath, ex);
        }

        _stdin = _ffmpeg.StandardInput.BaseStream;
        _ = ConsumeStderr(_ffmpeg);
        _logger.LogInformation("FFMpeg process started (PID {Pid}): {Args}", _ffmpeg.Id, args);
    }

    public void WriteFrame(byte[] frameData) => _stdin.Write(frameData, 0, frameData.Length);

    internal static string BuildArguments(int width, int height, int fps, string outputPath, string? microphoneDeviceName)
    {
        var hasMicrophone = !string.IsNullOrWhiteSpace(microphoneDeviceName);

        var args = new List<string>
        {
            "-y",
            "-f rawvideo",
            "-pix_fmt bgra",
            $"-s {width}x{height}",
            $"-r {fps}",
        };

        if (hasMicrophone)
        {
            args.Add("-use_wallclock_as_timestamps 1");
        }

        args.Add("-i pipe:0");

        if (hasMicrophone)
        {
            var microphoneDevice = microphoneDeviceName!;
            args.Add("-thread_queue_size 512");
            args.Add("-f dshow");
            args.Add("-audio_buffer_size 50");
            args.Add($"-i audio=\"{EscapeDirectShowDeviceName(microphoneDevice)}\"");
            args.Add("-map 0:v:0");
            args.Add("-map 1:a:0");
        }

        args.Add("-c:v libx264");
        args.Add("-preset ultrafast");
        args.Add("-crf 23");
        args.Add("-pix_fmt yuv420p");

        if (hasMicrophone)
        {
            args.Add("-c:a aac");
            args.Add("-b:a 128k");
            args.Add("-shortest");
        }

        args.Add($"\"{outputPath}\"");
        return string.Join(" ", args);
    }

    public void Dispose()
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

    private async Task ConsumeStderr(Process process)
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

    private static string EscapeDirectShowDeviceName(string microphoneDeviceName)
    {
        return microphoneDeviceName.Replace("\"", "\\\"");
    }

}
