using System.Diagnostics;

namespace Pointframe.Services;

public interface ISystemAudioMixer
{
    bool TryMix(string videoPath, string wavPath, bool videoHasMicrophone, TimeSpan? systemAudioOffset = null);
}

// Adds the system audio WAV to the finished MP4 (video stream-copied). The original file is replaced only after
// ffmpeg wrote a non-empty result; any failure leaves the original recording untouched.
public sealed class SystemAudioMixer : ISystemAudioMixer
{
    private static readonly TimeSpan MixTimeout = TimeSpan.FromMinutes(5);

    internal static readonly TimeSpan DurationTolerance = TimeSpan.FromMilliseconds(750);

    private readonly ILogger<SystemAudioMixer> _logger;
    private readonly Func<string, TimeSpan?> _probeDuration;
    private readonly Func<string, string, string, bool, TimeSpan?, bool>? _runFfmpeg;

    public SystemAudioMixer(ILogger<SystemAudioMixer> logger)
        : this(logger, ProbeDuration, null)
    {
    }

    internal SystemAudioMixer(ILogger<SystemAudioMixer> logger, Func<string, TimeSpan?> probeDuration, Func<string, string, string, bool, TimeSpan?, bool>? runFfmpeg)
    {
        _logger = logger;
        _probeDuration = probeDuration;
        _runFfmpeg = runFfmpeg;
    }

    public bool TryMix(string videoPath, string wavPath, bool videoHasMicrophone, TimeSpan? systemAudioOffset = null)
    {
        var temporaryPath = GetTemporaryPath(videoPath);
        try
        {
            File.Delete(temporaryPath);
            var ran = _runFfmpeg is not null
                ? _runFfmpeg(videoPath, wavPath, temporaryPath, videoHasMicrophone, systemAudioOffset)
                : RunFfmpeg(videoPath, wavPath, temporaryPath, videoHasMicrophone, systemAudioOffset);
            if (!ran || !File.Exists(temporaryPath) || new FileInfo(temporaryPath).Length == 0)
            {
                DeleteQuietly(temporaryPath);
                return false;
            }

            var originalDuration = _probeDuration(videoPath);
            var mixedDuration = _probeDuration(temporaryPath);
            if (!DurationsMatch(originalDuration, mixedDuration))
            {
                _logger.LogWarning("System audio mix changed the recording length ({Original} to {Mixed}); kept the original", originalDuration, mixedDuration);
                DeleteQuietly(temporaryPath);
                return false;
            }

            File.Move(temporaryPath, videoPath, overwrite: true);
            return true;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "System audio mix failed; the recording was kept without system audio");
            DeleteQuietly(temporaryPath);
            return false;
        }
    }

    internal static bool DurationsMatch(TimeSpan? original, TimeSpan? mixed)
    {
        return original is { } before && mixed is { } after && before > TimeSpan.Zero && (before - after).Duration() <= DurationTolerance;
    }

    private bool RunFfmpeg(string videoPath, string wavPath, string temporaryPath, bool videoHasMicrophone, TimeSpan? systemAudioOffset)
    {
        var ffmpeg = FfmpegResolver.ResolveRequired("System audio mixing");
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        BuildArguments(startInfo.ArgumentList, videoPath, wavPath, temporaryPath, videoHasMicrophone, systemAudioOffset);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("ffmpeg did not start.");
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(MixTimeout))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            _logger.LogWarning("System audio mix timed out");
            return false;
        }

        if (process.ExitCode != 0)
        {
            _logger.LogWarning("System audio mix failed with exit code {ExitCode}: {Stderr}", process.ExitCode, stderr.GetAwaiter().GetResult());
            return false;
        }

        return true;
    }

    private static TimeSpan? ProbeDuration(string path)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = FfmpegResolver.ResolveRequired("System audio mixing"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("-hide_banner");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(path);
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            return VideoTrimService.ParseDuration(stderr.GetAwaiter().GetResult());
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    internal static string GetTemporaryPath(string videoPath)
    {
        var directory = Path.GetDirectoryName(videoPath) ?? string.Empty;
        return Path.Combine(directory, $".{Path.GetFileNameWithoutExtension(videoPath)}.sysaudio.mp4");
    }

    internal static string GetWavPath(string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath) ?? string.Empty;
        return Path.Combine(directory, $".{Path.GetFileNameWithoutExtension(outputPath)}.sysaudio.wav");
    }

    internal static void BuildArguments(ICollection<string> args, string videoPath, string wavPath, string outputPath, bool videoHasMicrophone, TimeSpan? systemAudioOffset = null)
    {
        args.Add("-y");
        args.Add("-hide_banner");
        args.Add("-loglevel");
        args.Add("error");
        args.Add("-i");
        args.Add(videoPath);
        args.Add("-i");
        args.Add(wavPath);
        // The system audio is padded with silence without end, so the video (the only finite input
        // when there is no microphone) defines the output length and a short WAV cannot cut the recording.
        args.Add("-filter_complex");
        if (videoHasMicrophone)
        {
            args.Add($"[0:a:0]apad[mic];[1:a:0]{BuildSystemAudioShift(systemAudioOffset)}apad[sys];[mic][sys]amix=inputs=2:duration=first:dropout_transition=0[mix]");
        }
        else
        {
            args.Add("[1:a:0]apad[mix]");
        }

        args.Add("-map");
        args.Add("0:v:0");
        args.Add("-map");
        args.Add("[mix]");
        args.Add("-shortest");

        args.Add("-c:v");
        args.Add("copy");
        args.Add("-c:a");
        args.Add("aac");
        args.Add("-b:a");
        args.Add("128k");
        args.Add("-movflags");
        args.Add("+faststart");
        args.Add(outputPath);
    }

    // With a microphone the output timeline starts when ffmpeg first reads the video, which is later than the first
    // captured frame the WAV is aligned to. A positive offset trims that much from the WAV start; a negative one delays it.
    internal static string BuildSystemAudioShift(TimeSpan? offset)
    {
        if (offset is not { } shift || shift.Duration() < TimeSpan.FromMilliseconds(1))
        {
            return string.Empty;
        }

        var seconds = shift.TotalSeconds;
        return seconds > 0
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"atrim=start={seconds:0.###},asetpts=PTS-STARTPTS,")
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"adelay={-shift.TotalMilliseconds:0}:all=1,");
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
