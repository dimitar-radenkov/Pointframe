using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace Pointframe.Services;

public sealed class FFMpegVideoWriter : IVideoWriter
{
    private static readonly ConcurrentDictionary<string, bool> DrawtextSupportCache = new(StringComparer.OrdinalIgnoreCase);

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
        string? microphoneDeviceName = null,
        WatermarkSettings? videoWatermark = null,
        DateTimeOffset? watermarkTimestamp = null)
    {
        _logger = logger;

        var ffmpegPath = FfmpegResolver.ResolveRequired("Screen recording");
        var drawtextFilter = TryBuildDrawtextFilter(ffmpegPath, videoWatermark, watermarkTimestamp ?? DateTimeOffset.Now, logger);

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
        };
        BuildArguments(psi.ArgumentList, width, height, fps, outputPath, microphoneDeviceName, drawtextFilter);

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
        => BuildArguments(args, width, height, fps, outputPath, microphoneDeviceName, null);

    internal static void BuildArguments(
        ICollection<string> args,
        int width,
        int height,
        int fps,
        string outputPath,
        string? microphoneDeviceName,
        string? drawtextFilter)
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

        if (!string.IsNullOrWhiteSpace(drawtextFilter))
        {
            args.Add("-vf");
            args.Add(drawtextFilter);
        }

        args.Add("-c:v");
        args.Add("libx264");
        args.Add("-preset");
        args.Add("ultrafast");
        args.Add("-crf");
        args.Add("23");
        args.Add("-pix_fmt");
        args.Add("yuv420p");
        // Keyframe every second so stream-copy trims can cut close to the requested point.
        args.Add("-g");
        args.Add($"{fps}");

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

    internal static string BuildDrawtextFilter(
        WatermarkSettings settings,
        DateTimeOffset timestamp,
        string appName,
        string fontFilePath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(appName);
        ArgumentException.ThrowIfNullOrWhiteSpace(fontFilePath);

        var rawText = WatermarkTokenResolver.Resolve(settings.TextTemplate, timestamp)
            .Replace("{app}", appName, StringComparison.OrdinalIgnoreCase);
        var escapedText = EscapeDrawtextValue(rawText);
        var escapedFontFile = EscapeDrawtextValue(fontFilePath.Replace('\\', '/'));
        var fontColor = ToDrawtextColor(settings.ColorHex, settings.Opacity);
        var fontSize = Math.Max(1, (int)Math.Round(settings.FontSize));
        var margin = Math.Max(0, (int)Math.Round(settings.Margin));
        var (x, y) = GetDrawtextCoordinates(settings.Position, margin);
        var box = settings.BackgroundEnabled ? "1" : "0";

        return $"drawtext=fontfile='{escapedFontFile}':text='{escapedText}':x={x}:y={y}:fontsize={fontSize}:fontcolor={fontColor}:box={box}:boxcolor=black@0.45:boxborderw=12";
    }

    internal static string EscapeDrawtextValue(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(":", "\\:", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);
    }

    private static string? TryBuildDrawtextFilter(
        string ffmpegPath,
        WatermarkSettings? settings,
        DateTimeOffset timestamp,
        ILogger logger)
    {
        if (settings is not { Enabled: true })
        {
            return null;
        }

        var fontFilePath = ResolveFontFilePath();
        if (fontFilePath is null)
        {
            logger.LogWarning("Video watermark requested, but no Windows font file was found. Recording will continue without watermark.");
            return null;
        }

        if (!HasDrawtextFilter(ffmpegPath, logger))
        {
            logger.LogWarning("Video watermark requested, but ffmpeg drawtext filter is unavailable. Recording will continue without watermark.");
            return null;
        }

        return BuildDrawtextFilter(settings, timestamp, "Pointframe", fontFilePath);
    }

    private static bool HasDrawtextFilter(string ffmpegPath, ILogger logger)
    {
        return DrawtextSupportCache.GetOrAdd(ffmpegPath, path => ProbeHasDrawtextFilter(path, logger));
    }

    private static bool ProbeHasDrawtextFilter(string ffmpegPath, ILogger logger)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            process.StartInfo.ArgumentList.Add("-hide_banner");
            process.StartInfo.ArgumentList.Add("-h");
            process.StartInfo.ArgumentList.Add("filter=drawtext");

            process.Start();
            if (!process.WaitForExit(3000))
            {
                process.Kill();
                logger.LogWarning("Timed out while probing ffmpeg filters; video watermark disabled for this recording.");
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to probe ffmpeg drawtext support; video watermark disabled for this recording.");
            return false;
        }
    }

    private static string? ResolveFontFilePath()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(windows))
        {
            return null;
        }

        var candidates = new[]
        {
            Path.Combine(windows, "Fonts", "segoeui.ttf"),
            Path.Combine(windows, "Fonts", "arial.ttf"),
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static (string X, string Y) GetDrawtextCoordinates(WatermarkPosition position, int margin)
    {
        var marginText = margin.ToString(CultureInfo.InvariantCulture);
        return position switch
        {
            WatermarkPosition.TopLeft => (marginText, marginText),
            WatermarkPosition.TopRight => ($"w-tw-{marginText}", marginText),
            WatermarkPosition.BottomLeft => (marginText, $"h-th-{marginText}"),
            WatermarkPosition.BottomRight => ($"w-tw-{marginText}", $"h-th-{marginText}"),
            WatermarkPosition.Center => ("(w-tw)/2", "(h-th)/2"),
            _ => ($"w-tw-{marginText}", $"h-th-{marginText}"),
        };
    }

    private static string ToDrawtextColor(string colorHex, double opacity)
    {
        var normalizedOpacity = Math.Clamp(opacity, 0d, 1d);
        if (TryParseArgb(colorHex, out var a, out var r, out var g, out var b))
        {
            var alpha = Math.Clamp((a / 255d) * normalizedOpacity, 0d, 1d);
            return $"#{r:X2}{g:X2}{b:X2}@{alpha.ToString("0.###", CultureInfo.InvariantCulture)}";
        }

        return $"white@{normalizedOpacity.ToString("0.###", CultureInfo.InvariantCulture)}";
    }

    private static bool TryParseArgb(string? colorHex, out byte a, out byte r, out byte g, out byte b)
    {
        a = 255;
        r = 255;
        g = 255;
        b = 255;

        if (string.IsNullOrWhiteSpace(colorHex))
        {
            return false;
        }

        var normalized = colorHex.Trim();
        if (normalized.StartsWith("#", StringComparison.Ordinal))
        {
            normalized = normalized[1..];
        }

        if (normalized.Length == 8)
        {
            return byte.TryParse(normalized.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out a) &&
                   byte.TryParse(normalized.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r) &&
                   byte.TryParse(normalized.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g) &&
                   byte.TryParse(normalized.AsSpan(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b);
        }

        if (normalized.Length == 6)
        {
            a = 255;
            return byte.TryParse(normalized.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r) &&
                   byte.TryParse(normalized.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g) &&
                   byte.TryParse(normalized.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b);
        }

        return false;
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
