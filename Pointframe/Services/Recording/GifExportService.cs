using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Pointframe.Services;

public sealed class GifExportService : IGifExportService
{
    private readonly ILogger<GifExportService> _logger;

    public GifExportService(ILogger<GifExportService> logger)
    {
        _logger = logger;
    }

    public async Task Export(string inputPath, string outputPath, int fps, CancellationToken ct = default)
    {
        var ffmpegPath = FfmpegResolver.ResolveRequired("GIF export");

        // Two-pass palette filtergraph: palettegen + paletteuse gives near-lossless colour fidelity
        // compared to the default 256-colour GIF quantisation.
        var filter = $"fps={fps},split[s0][s1];[s0]palettegen=stats_mode=diff[p];[s1][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle";

        _logger.LogInformation("Starting GIF export: {Input} → {Output} @ {Fps}fps", inputPath, outputPath, fps);

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(inputPath);
        psi.ArgumentList.Add("-vf");
        psi.ArgumentList.Add(filter);
        psi.ArgumentList.Add(outputPath);

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();
        }
        catch (Win32Exception ex) when (string.Equals(ffmpegPath, "ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw FfmpegResolver.CreateMissingException("GIF export", ffmpegPath, ex);
        }

        var consumeTask = ConsumeStderr(process);

        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        await consumeTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            _logger.LogError("ffmpeg GIF export exited with code {Code}", process.ExitCode);
            throw new InvalidOperationException($"ffmpeg exited with code {process.ExitCode} during GIF export.");
        }

        _logger.LogInformation("GIF export complete: {Output}", outputPath);
    }

    private async Task ConsumeStderr(Process process)
    {
        try
        {
            var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                _logger.LogDebug("ffmpeg GIF stderr: {Stderr}", stderr);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read ffmpeg GIF stderr");
        }
    }
}
