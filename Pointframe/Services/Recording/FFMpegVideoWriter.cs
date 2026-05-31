using System.ComponentModel;
using System.Diagnostics;

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

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
        };
        BuildArguments(psi.ArgumentList, width, height, fps, outputPath, microphoneDeviceName);

        _ffmpeg = new Process { StartInfo = psi };

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
        _logger.LogInformation("FFMpeg process started (PID {Pid})", _ffmpeg.Id);
    }

    public void WriteFrame(byte[] frameData) => _stdin.Write(frameData, 0, frameData.Length);

    internal static void BuildArguments(ICollection<string> args, int width, int height, int fps, string outputPath, string? microphoneDeviceName)
    {
        var hasMicrophone = !string.IsNullOrWhiteSpace(microphoneDeviceName);

        args.Add("-y");
        args.Add("-f");
        args.Add("rawvideo");
        args.Add("-pix_fmt");
        args.Add("bgra");
        args.Add("-s");
        args.Add($"{width}x{height}");
        args.Add("-r");
        args.Add($"{fps}");

        if (hasMicrophone)
        {
            args.Add("-use_wallclock_as_timestamps");
            args.Add("1");
        }

        args.Add("-i");
        args.Add("pipe:0");

        if (hasMicrophone)
        {
            args.Add("-thread_queue_size");
            args.Add("512");
            args.Add("-f");
            args.Add("dshow");
            args.Add("-audio_buffer_size");
            args.Add("50");
            args.Add("-i");
            args.Add($"audio={microphoneDeviceName}");
            args.Add("-map");
            args.Add("0:v:0");
            args.Add("-map");
            args.Add("1:a:0");
        }

        args.Add("-c:v");
        args.Add("libx264");
        args.Add("-preset");
        args.Add("ultrafast");
        args.Add("-crf");
        args.Add("23");
        args.Add("-pix_fmt");
        args.Add("yuv420p");

        if (hasMicrophone)
        {
            args.Add("-c:a");
            args.Add("aac");
            args.Add("-b:a");
            args.Add("128k");
            args.Add("-shortest");
        }

        args.Add(outputPath);
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
            string? line;
            while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("warning", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("failed", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("ffmpeg: {Line}", line);
                }
                else
                {
                    _logger.LogDebug("ffmpeg: {Line}", line);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read ffmpeg stderr");
        }
    }
}
