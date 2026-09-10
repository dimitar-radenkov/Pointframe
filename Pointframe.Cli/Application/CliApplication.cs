using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pointframe.Data;
using Pointframe.Engine;

namespace Pointframe.Cli;

internal sealed class CliApplication
{
    private const int SchemaVersion = 1;

    private readonly IDirectCaptureService _directCaptureService;
    private readonly IDirectRecordingService _directRecordingService;
    private readonly TextWriter _standardOutput;
    private readonly TextWriter _standardError;

    internal CliApplication(
        IDirectCaptureService directCaptureService,
        IDirectRecordingService directRecordingService,
        TextWriter standardOutput,
        TextWriter standardError)
    {
        _directCaptureService = directCaptureService;
        _directRecordingService = directRecordingService;
        _standardOutput = standardOutput;
        _standardError = standardError;
    }

    internal static async Task<int> RunAsync(string[] args, TextWriter standardOutput, TextWriter standardError, CancellationToken cancellationToken = default)
    {
        try
        {
            // Parse before constructing any capture/recording/OCR service so --help/--version
            // (and usage errors) never depend on those services being constructible.
            if (!CliCommandParser.TryParse(args, out var command, out var parseError))
            {
                await standardError.WriteLineAsync(parseError);
                await standardError.WriteLineAsync(CliCommandParser.Usage);
                return 2;
            }

            if (string.Equals(command.Name, "help", StringComparison.Ordinal))
            {
                await standardOutput.WriteLineAsync(CliCommandParser.HelpText);
                return 0;
            }

            if (string.Equals(command.Name, "version", StringComparison.Ordinal))
            {
                await standardOutput.WriteLineAsync(GetVersion());
                return 0;
            }

            Directory.CreateDirectory(PointframePaths.LocalAppDataDirectory);
            using var serviceProvider = new ServiceCollection()
                .AddPointframeDataServices($"Data Source={PointframePaths.PointframeDatabasePath}")
                .AddSingleton(TimeProvider.System)
                .AddSingleton<ICaptureCatalogService, CaptureCatalogService>()
                .AddSingleton<ICaptureRegistrationService, CaptureRegistrationService>()
                .BuildServiceProvider();
            var directCaptureService = new DirectCaptureService(
                new DisplayCaptureEngine(),
                ocrEngineService: new WindowsOcrEngineService(),
                captureCatalogService: serviceProvider.GetRequiredService<ICaptureCatalogService>(),
                captureRegistrationService: serviceProvider.GetRequiredService<ICaptureRegistrationService>());
            using var directRecordingService = new DirectRecordingService(new DisplayCaptureEngine(), new FfmpegDirectVideoWriterFactory());
            return await new CliApplication(directCaptureService, directRecordingService, standardOutput, standardError).RunCommandAsync(command, cancellationToken);
        }
        catch (Exception exception)
        {
            return await WriteFailureResponseAsync(standardOutput, standardError, exception);
        }
    }

    internal async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        if (!CliCommandParser.TryParse(args, out var command, out var error))
        {
            await _standardError.WriteLineAsync(error);
            await _standardError.WriteLineAsync(CliCommandParser.Usage);
            return 2;
        }

        return await RunCommandAsync(command, cancellationToken);
    }

    internal async Task<int> RunCommandAsync(CliCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.Equals(command.Name, "help", StringComparison.Ordinal))
            {
                await _standardOutput.WriteLineAsync(CliCommandParser.HelpText);
                return 0;
            }

            if (string.Equals(command.Name, "version", StringComparison.Ordinal))
            {
                await _standardOutput.WriteLineAsync(GetVersion());
                return 0;
            }

            if (string.Equals(command.Name, "record", StringComparison.Ordinal))
            {
                return await RunRecordAsync(command, cancellationToken);
            }

            var payload = command.Name switch
            {
                "displays" => _directCaptureService.ListDisplays(),
                "windows" => _directCaptureService.ListWindows(),
                "capture" => await _directCaptureService.CaptureMonitorAsync(command.MonitorName!, command.Region, command.OutputPath, cancellationToken),
                "ocr" => await _directCaptureService.CaptureMonitorTextAsync(command.MonitorName!, command.Region, command.OutputPath, cancellationToken),
                "capture-window" => await _directCaptureService.CaptureWindowAsync(command.WindowId!.Value, command.OutputPath, cancellationToken),
                "ocr-window" => await _directCaptureService.CaptureWindowTextAsync(command.WindowId!.Value, command.OutputPath, cancellationToken),
                _ => throw new InvalidOperationException($"Unsupported CLI command '{command.Name}'."),
            };

            await _standardOutput.WriteLineAsync(payload);
            return 0;
        }
        catch (Exception exception)
        {
            return await WriteFailureResponseAsync(_standardOutput, _standardError, exception);
        }
    }

    // Writes a runtime failure as the same single-line JSON DirectCaptureResponse shape the success path
    // uses, so a caller parsing standard output never has to fall back to scraping the human-readable
    // stderr line to discover that (and why) a command failed.
    private static async Task<int> WriteFailureResponseAsync(TextWriter standardOutput, TextWriter standardError, Exception exception)
    {
        await standardError.WriteLineAsync($"Pointframe CLI failed: {exception.Message}");
        var response = new DirectCaptureResponse(
            SchemaVersion,
            false,
            new DirectCaptureError(ToErrorCode(exception), exception.Message));
        await standardOutput.WriteLineAsync(JsonSerializer.Serialize(response));
        return 1;
    }

    private static string ToErrorCode(Exception exception)
    {
        // Order matters. A rejected --output value arrives as an ArgumentException just like a missing
        // monitor or window does, so it has to be separated by parameter name first or scripts would be
        // told the target was not found. ArgumentOutOfRangeException then has to precede ArgumentException
        // because it derives from it.
        return exception switch
        {
            ArgumentException argument when string.Equals(argument.ParamName, "outputPath", StringComparison.Ordinal) => "invalid_output_path",
            ArgumentOutOfRangeException => "invalid_region",
            ArgumentException => "target_not_found",
            InvalidOperationException => "target_not_capturable",
            OperationCanceledException => "canceled",
            _ => "capture_failed",
        };
    }

    private async Task<int> RunRecordAsync(CliCommand command, CancellationToken cancellationToken)
    {
        var startResult = _directRecordingService.Start(new DirectRecordingRequest(
            command.MonitorName!,
            command.RedactionRegions ?? Array.Empty<PixelBounds>(),
            command.FramesPerSecond,
            OutputPath: command.OutputPath));

        if (!startResult.Success)
        {
            var failureResponse = new DirectRecordingResponse(
                SchemaVersion,
                false,
                new DirectCaptureError(startResult.ErrorCode ?? "recording_start_failed", startResult.ErrorMessage ?? "Unknown error."));
            await _standardOutput.WriteLineAsync(JsonSerializer.Serialize(failureResponse));
            return 1;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(command.RecordSeconds!.Value), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C (or another external cancellation) requested an early, graceful stop.
        }

        var stopResult = await _directRecordingService.StopAsync(CancellationToken.None);
        var response = new DirectRecordingResponse(
            SchemaVersion,
            stopResult.Success,
            stopResult.ErrorCode is null ? null : new DirectCaptureError(stopResult.ErrorCode, stopResult.ErrorMessage ?? "Unknown error."),
            startResult.Session,
            stopResult.Artifact);
        await _standardOutput.WriteLineAsync(JsonSerializer.Serialize(response));
        return stopResult.Success ? 0 : 1;
    }

    private static string GetVersion()
    {
        // Assembly.Location is empty for single-file publishes; use Environment.ProcessPath instead.
        var location = Environment.ProcessPath ?? Assembly.GetEntryAssembly()?.Location;
        if (!string.IsNullOrEmpty(location))
        {
            var fileVersionInfo = FileVersionInfo.GetVersionInfo(location);
            if (!string.IsNullOrWhiteSpace(fileVersionInfo.ProductVersion))
            {
                return $"Pointframe CLI {fileVersionInfo.ProductVersion}";
            }
        }

        var assemblyVersion = Assembly.GetEntryAssembly()?.GetName().Version;
        return $"Pointframe CLI {assemblyVersion?.ToString() ?? "0.0.0"}";
    }
}
