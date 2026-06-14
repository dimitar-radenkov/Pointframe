using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Pointframe.Services;

public sealed partial class VideoTrimService : IVideoTrimService
{
    // Stream copy can only cut video at keyframes, so the output may start earlier than
    // requested. Accept up to this much extra content before falling back to a re-encode.
    private static readonly TimeSpan CopyDurationTolerance = TimeSpan.FromSeconds(0.5);

    private readonly ILogger<VideoTrimService> _logger;

    public VideoTrimService(ILogger<VideoTrimService> logger)
    {
        _logger = logger;
    }

    public async Task Trim(string inputPath, string outputPath, TimeSpan start, TimeSpan end, CancellationToken ct = default)
    {
        if (end <= start)
        {
            throw new ArgumentOutOfRangeException(nameof(end), end, "Trim end must be after trim start.");
        }

        var ffmpegPath = FfmpegResolver.ResolveRequired("Video trim");

        _logger.LogInformation("Starting trim: {Input} → {Output} ({Start}–{End})", inputPath, outputPath, start, end);

        // Stream copy gives an instant, lossless trim but only cuts on keyframes; if it fails or
        // the cut lands too far from the requested start, fall back to a re-encode which is
        // slower but frame-accurate.
        if (await RunFfmpeg(ffmpegPath, inputPath, outputPath, start, end, reEncode: false, ct).ConfigureAwait(false) &&
            await CopyTrimIsAccurate(ffmpegPath, outputPath, end - start, ct).ConfigureAwait(false))
        {
            _logger.LogInformation("Trim complete (stream copy): {Output}", outputPath);
            return;
        }

        _logger.LogInformation("Stream-copy trim unusable for {Input}; retrying with re-encode", inputPath);
        DeleteIfExists(outputPath);

        if (!await RunFfmpeg(ffmpegPath, inputPath, outputPath, start, end, reEncode: true, ct).ConfigureAwait(false))
        {
            DeleteIfExists(outputPath);
            throw new InvalidOperationException("ffmpeg failed to trim the recording.");
        }

        _logger.LogInformation("Trim complete (re-encode): {Output}", outputPath);
    }

    internal static string GetDefaultOutputPath(string inputPath)
    {
        var directory = Path.GetDirectoryName(inputPath) ?? string.Empty;
        var baseName = Path.GetFileNameWithoutExtension(inputPath);
        var extension = Path.GetExtension(inputPath);

        var candidate = Path.Combine(directory, $"{baseName}.trimmed{extension}");
        for (var counter = 2; File.Exists(candidate); counter++)
        {
            candidate = Path.Combine(directory, $"{baseName}.trimmed-{counter}{extension}");
        }

        return candidate;
    }

    internal static void BuildArguments(ICollection<string> args, string inputPath, string outputPath, TimeSpan start, TimeSpan end, bool reEncode)
    {
        args.Add("-y");
        args.Add("-ss");
        args.Add(FormatSeconds(start));
        args.Add("-i");
        args.Add(inputPath);
        args.Add("-t");
        args.Add(FormatSeconds(end - start));

        if (reEncode)
        {
            args.Add("-c:v");
            args.Add("libx264");
            args.Add("-preset");
            args.Add("veryfast");
            args.Add("-crf");
            args.Add("23");
            args.Add("-pix_fmt");
            args.Add("yuv420p");
            args.Add("-c:a");
            args.Add("aac");
            args.Add("-b:a");
            args.Add("128k");
        }
        else
        {
            args.Add("-c");
            args.Add("copy");
            args.Add("-avoid_negative_ts");
            args.Add("make_zero");
        }

        args.Add(outputPath);
    }

    private async Task<bool> RunFfmpeg(string ffmpegPath, string inputPath, string outputPath, TimeSpan start, TimeSpan end, bool reEncode, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        BuildArguments(psi.ArgumentList, inputPath, outputPath, start, end, reEncode);

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();
        }
        catch (Win32Exception ex) when (string.Equals(ffmpegPath, "ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw FfmpegResolver.CreateMissingException("Video trim", ffmpegPath, ex);
        }

        var consumeTask = ConsumeStderr(process);

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await TerminateProcessIfRunning(process, "trim").ConfigureAwait(false);
            throw;
        }
        finally
        {
            await consumeTask.ConfigureAwait(false);
        }

        if (process.ExitCode != 0)
        {
            _logger.LogWarning("ffmpeg trim ({Mode}) exited with code {Code}", reEncode ? "re-encode" : "copy", process.ExitCode);
            return false;
        }

        var output = new FileInfo(outputPath);
        return output.Exists && output.Length > 0;
    }

    private async Task<bool> CopyTrimIsAccurate(string ffmpegPath, string outputPath, TimeSpan requestedDuration, CancellationToken ct)
    {
        var actualDuration = await ProbeDuration(ffmpegPath, outputPath, ct).ConfigureAwait(false);
        if (actualDuration is null)
        {
            // Cannot verify — keep the lossless result rather than re-encoding blindly.
            return true;
        }

        var deviation = actualDuration.Value - requestedDuration;
        if (deviation <= CopyDurationTolerance)
        {
            return true;
        }

        _logger.LogInformation(
            "Stream-copy trim is {Deviation:0.##}s longer than requested (keyframe-bound cut)",
            deviation.TotalSeconds);
        return false;
    }

    private async Task<TimeSpan?> ProbeDuration(string ffmpegPath, string path, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(path);

        using var process = new Process { StartInfo = psi };

        try
        {
            // ffmpeg exits non-zero without an output file; the header dump on stderr
            // is all we need.
            process.Start();
            var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return ParseDuration(stderr);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            await TerminateProcessIfRunning(process, "probe").ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to probe trimmed output duration: {Path}", path);
            return null;
        }
    }

    internal static TimeSpan? ParseDuration(string ffmpegOutput)
    {
        var match = DurationRegex().Match(ffmpegOutput);
        if (!match.Success)
        {
            return null;
        }

        return new TimeSpan(0,
                int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture))
            + TimeSpan.FromSeconds(int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) / 100.0);
    }

    [GeneratedRegex(@"Duration:\s*(\d+):(\d{2}):(\d{2})\.(\d{2})")]
    private static partial Regex DurationRegex();

    private async Task ConsumeStderr(Process process)
    {
        try
        {
            var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                _logger.LogDebug("ffmpeg trim stderr: {Stderr}", stderr);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read ffmpeg trim stderr");
        }
    }

    private async Task TerminateProcessIfRunning(Process process, string operation)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to terminate ffmpeg process after cancellation during {Operation}", operation);
        }
    }

    private void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete partial trim output: {Path}", path);
        }
    }

    private static string FormatSeconds(TimeSpan value) =>
        value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
}
