using System.ComponentModel;
using System.Diagnostics;

namespace Pointframe.Services;

public sealed class FfmpegAudioExtractor : IAudioExtractor
{
    private readonly ILogger<FfmpegAudioExtractor> _logger;

    public FfmpegAudioExtractor(ILogger<FfmpegAudioExtractor> logger)
    {
        _logger = logger;
    }

    public async Task<string> ExtractWavAsync(string videoPath, CancellationToken ct)
    {
        var ffmpegPath = FfmpegResolver.ResolveRequired("Transcript audio extraction");
        var outputWavPath = Path.Combine(Path.GetTempPath(), $"pointframe-transcript-{Guid.NewGuid():N}.wav");

        _logger.LogInformation("Extracting audio for transcription: {Video} → {Wav}", videoPath, outputWavPath);

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        BuildArguments(psi.ArgumentList, videoPath, outputWavPath);

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();
        }
        catch (Win32Exception ex) when (string.Equals(ffmpegPath, "ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw FfmpegResolver.CreateMissingException("Transcript audio extraction", ffmpegPath, ex);
        }

        var consumeTask = ConsumeStderr(process);

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await TerminateProcessIfRunning(process).ConfigureAwait(false);
            DeleteIfExists(outputWavPath);
            throw;
        }
        finally
        {
            await consumeTask.ConfigureAwait(false);
        }

        if (process.ExitCode != 0)
        {
            DeleteIfExists(outputWavPath);
            throw new InvalidOperationException($"ffmpeg exited with code {process.ExitCode} while extracting audio.");
        }

        var output = new FileInfo(outputWavPath);
        if (!output.Exists || output.Length == 0)
        {
            DeleteIfExists(outputWavPath);
            throw new InvalidOperationException("ffmpeg produced no audio output. The recording may not contain an audio track.");
        }

        return outputWavPath;
    }

    internal static void BuildArguments(ICollection<string> args, string inputPath, string outputWavPath)
    {
        args.Add("-y");
        args.Add("-i");
        args.Add(inputPath);
        args.Add("-vn");
        args.Add("-ac");
        args.Add("1");
        args.Add("-ar");
        args.Add("16000");
        args.Add("-c:a");
        args.Add("pcm_s16le");
        args.Add(outputWavPath);
    }

    private async Task ConsumeStderr(Process process)
    {
        try
        {
            var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                _logger.LogDebug("ffmpeg audio extraction stderr: {Stderr}", stderr);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read ffmpeg audio extraction stderr");
        }
    }

    private async Task TerminateProcessIfRunning(Process process)
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
            _logger.LogWarning(ex, "Failed to terminate ffmpeg process after cancellation during audio extraction");
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
            _logger.LogWarning(ex, "Failed to delete temp WAV file {Path}", path);
        }
    }
}
