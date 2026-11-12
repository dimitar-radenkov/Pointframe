using System.Buffers.Binary;
using System.Diagnostics;

namespace Pointframe.Services;

public interface IRecordingRemuxer
{
    TimeSpan? Remux(string intermediatePath, string outputPath);
    TimeSpan? ReadDuration(string path);
}

public sealed record RecoveredRecording(string FilePath, TimeSpan Duration, bool Remuxed);

public sealed class RecordingRecovery(IRecordingRemuxer remuxer, ILogger<RecordingRecovery> logger)
{
    internal const string IntermediateSuffix = ".recording.mp4";
    private const uint MoofBox = 0x6D6F6F66;
    private const uint MdatBox = 0x6D646174;

    public static string GetIntermediatePath(string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath) ?? string.Empty;
        return Path.Combine(directory, $".{Path.GetFileNameWithoutExtension(outputPath)}{IntermediateSuffix}");
    }

    public RecoveredRecording? Recover(string intermediatePath, string outputPath)
    {
        try
        {
            if (!ContainsCompleteFragment(intermediatePath))
            {
                DeleteQuietly(intermediatePath);
                return null;
            }

            if (!File.Exists(outputPath))
            {
                var duration = remuxer.Remux(intermediatePath, outputPath);
                if (duration is not null)
                {
                    DeleteQuietly(intermediatePath);
                    return new RecoveredRecording(outputPath, duration.Value, true);
                }
            }

            var recoveredPath = PreserveAsRecovered(intermediatePath, outputPath);
            logger.LogWarning("Recording could not be remuxed; kept the fragmented file at {Path}", recoveredPath);
            return new RecoveredRecording(recoveredPath, remuxer.ReadDuration(recoveredPath) ?? TimeSpan.Zero, false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not recover recording intermediate {Path}", intermediatePath);
            return null;
        }
    }

    public int RecoverLeftovers(string directory, DateTime sessionStartedUtc)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return 0;
        }

        var recovered = 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, $"*{IntermediateSuffix}").ToList())
            {
                if (!TryGetOutputPath(path, out var outputPath)
                    || File.GetLastWriteTimeUtc(path) >= sessionStartedUtc
                    || IsOpenElsewhere(path))
                {
                    continue;
                }

                var result = Recover(path, outputPath);
                if (File.Exists(path))
                {
                    TryPreserve(path, outputPath);
                }

                if (result is null)
                {
                    logger.LogInformation("Leftover recording intermediate yielded no playable data");
                    continue;
                }

                recovered++;
                logger.LogInformation("Recovered leftover recording at {Path} ({Duration:c}, remuxed {Remuxed})", result.FilePath, result.Duration, result.Remuxed);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Leftover recording recovery stopped early");
        }

        return recovered;
    }

    internal static bool TryGetOutputPath(string intermediatePath, out string outputPath)
    {
        outputPath = string.Empty;
        var fileName = Path.GetFileName(intermediatePath);
        if (fileName.Length <= IntermediateSuffix.Length + 1
            || fileName[0] != '.'
            || !fileName.EndsWith(IntermediateSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var stem = fileName[1..^IntermediateSuffix.Length];
        outputPath = Path.Combine(Path.GetDirectoryName(intermediatePath) ?? string.Empty, stem + ".mp4");
        return true;
    }

    internal static bool ContainsCompleteFragment(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> header = stackalloc byte[16];
        var seenMoof = false;
        long position = 0;
        while (position + 8 <= stream.Length)
        {
            stream.Position = position;
            if (stream.Read(header[..8]) < 8)
            {
                return false;
            }

            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
            if (size == 1)
            {
                if (stream.Read(header[8..16]) < 8)
                {
                    return false;
                }

                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
            }

            if (size < 8)
            {
                return false;
            }

            if (type == MoofBox)
            {
                seenMoof = true;
            }
            else if (type == MdatBox && seenMoof && position + size <= stream.Length)
            {
                return true;
            }

            position += size;
        }

        return false;
    }

    internal static string PreserveAsRecovered(string intermediatePath, string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(outputPath);
        var recoveredPath = Path.Combine(directory, $"{stem}.recovered.mp4");
        for (var attempt = 2; File.Exists(recoveredPath); attempt++)
        {
            recoveredPath = Path.Combine(directory, $"{stem}.recovered-{attempt}.mp4");
        }

        File.Move(intermediatePath, recoveredPath);
        return recoveredPath;
    }

    private void TryPreserve(string intermediatePath, string outputPath)
    {
        try
        {
            logger.LogWarning("Leftover recording kept at {Path}", PreserveAsRecovered(intermediatePath, outputPath));
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not rename leftover recording {Path}", intermediatePath);
        }
    }

    private static bool IsOpenElsewhere(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public sealed class FfmpegRecordingRemuxer(ILogger<FfmpegRecordingRemuxer> logger) : IRecordingRemuxer
{
    private static readonly TimeSpan RemuxTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    public TimeSpan? Remux(string intermediatePath, string outputPath)
    {
        var temporaryPath = Path.Combine(Path.GetDirectoryName(outputPath) ?? string.Empty, $".{Path.GetFileNameWithoutExtension(outputPath)}.remux.mp4");
        try
        {
            var ffmpeg = FfmpegResolver.ResolveRequired("Recording recovery");
            File.Delete(temporaryPath);
            var (exitCode, stderr) = Run(ffmpeg, ["-y", "-hide_banner", "-loglevel", "error", "-i", intermediatePath, "-c", "copy", "-movflags", "+faststart", temporaryPath], RemuxTimeout);
            if (exitCode != 0 || !File.Exists(temporaryPath) || new FileInfo(temporaryPath).Length == 0)
            {
                logger.LogWarning("Recording remux failed with exit code {ExitCode}: {Stderr}", exitCode, stderr);
                File.Delete(temporaryPath);
                return null;
            }

            var duration = ReadDuration(temporaryPath);
            if (duration is null || duration <= TimeSpan.Zero)
            {
                logger.LogWarning("Remuxed recording has no readable duration");
                File.Delete(temporaryPath);
                return null;
            }

            File.Move(temporaryPath, outputPath);
            return duration;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Recording remux failed");
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException)
            {
            }

            return null;
        }
    }

    public TimeSpan? ReadDuration(string path)
    {
        try
        {
            var ffmpeg = FfmpegResolver.ResolveRequired("Recording recovery");
            var (_, stderr) = Run(ffmpeg, ["-hide_banner", "-i", path], ProbeTimeout);
            return VideoTrimService.ParseDuration(stderr);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not read the duration of {Path}", path);
            return null;
        }
    }

    private static (int ExitCode, string Stderr) Run(string ffmpeg, string[] arguments, TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("ffmpeg did not start.");
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeout))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            return (-1, "ffmpeg timed out.");
        }

        return (process.ExitCode, stderr.GetAwaiter().GetResult());
    }
}
