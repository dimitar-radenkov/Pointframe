using System.Diagnostics;

namespace Pointframe.Services;

public interface ISystemAudioMixer
{
    bool TryMix(string videoPath, string wavPath, bool videoHasMicrophone);
}

// Adds the system audio WAV to the finished MP4 (video stream-copied). The original file is replaced only after
// ffmpeg wrote a non-empty result; any failure leaves the original recording untouched.
public sealed class SystemAudioMixer : ISystemAudioMixer
{
    private static readonly TimeSpan MixTimeout = TimeSpan.FromMinutes(5);

    private readonly ILogger<SystemAudioMixer> _logger;

    public SystemAudioMixer(ILogger<SystemAudioMixer> logger)
    {
        _logger = logger;
    }

    public bool TryMix(string videoPath, string wavPath, bool videoHasMicrophone)
    {
        var temporaryPath = GetTemporaryPath(videoPath);
        try
        {
            var ffmpeg = FfmpegResolver.ResolveRequired("System audio mixing");
            File.Delete(temporaryPath);
            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpeg,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            BuildArguments(startInfo.ArgumentList, videoPath, wavPath, temporaryPath, videoHasMicrophone);

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("ffmpeg did not start.");
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(MixTimeout))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                _logger.LogWarning("System audio mix timed out");
                DeleteQuietly(temporaryPath);
                return false;
            }

            if (process.ExitCode != 0 || !File.Exists(temporaryPath) || new FileInfo(temporaryPath).Length == 0)
            {
                _logger.LogWarning("System audio mix failed with exit code {ExitCode}: {Stderr}", process.ExitCode, stderr.GetAwaiter().GetResult());
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

    internal static void BuildArguments(ICollection<string> args, string videoPath, string wavPath, string outputPath, bool videoHasMicrophone)
    {
        args.Add("-y");
        args.Add("-hide_banner");
        args.Add("-loglevel");
        args.Add("error");
        args.Add("-i");
        args.Add(videoPath);
        args.Add("-i");
        args.Add(wavPath);
        if (videoHasMicrophone)
        {
            args.Add("-filter_complex");
            args.Add("[0:a:0][1:a:0]amix=inputs=2:duration=first:dropout_transition=0[mix]");
            args.Add("-map");
            args.Add("0:v:0");
            args.Add("-map");
            args.Add("[mix]");
        }
        else
        {
            args.Add("-map");
            args.Add("0:v:0");
            args.Add("-map");
            args.Add("1:a:0");
            args.Add("-shortest");
        }

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
