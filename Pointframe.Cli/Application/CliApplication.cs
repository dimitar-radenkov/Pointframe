using System.Text.Json;
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
            var directCaptureService = new DirectCaptureService(new DisplayCaptureEngine(), ocrEngineService: new WindowsOcrEngineService());
            using var directRecordingService = new DirectRecordingService(new DisplayCaptureEngine(), new FfmpegDirectVideoWriterFactory());
            return await new CliApplication(directCaptureService, directRecordingService, standardOutput, standardError).RunAsync(args, cancellationToken);
        }
        catch (Exception exception)
        {
            await standardError.WriteLineAsync($"Pointframe CLI failed: {exception.Message}");
            return 1;
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

        try
        {
            if (string.Equals(command.Name, "record", StringComparison.Ordinal))
            {
                return await RunRecordAsync(command, cancellationToken);
            }

            var payload = command.Name switch
            {
                "displays" => _directCaptureService.ListDisplays(),
                "capture" => await _directCaptureService.CaptureMonitorAsync(command.MonitorName!, cancellationToken),
                "ocr" => await _directCaptureService.CaptureMonitorTextAsync(command.MonitorName!, cancellationToken),
                _ => throw new InvalidOperationException($"Unsupported CLI command '{command.Name}'."),
            };

            await _standardOutput.WriteLineAsync(payload);
            return 0;
        }
        catch (Exception exception)
        {
            await _standardError.WriteLineAsync($"Pointframe CLI failed: {exception.Message}");
            return 1;
        }
    }

    private async Task<int> RunRecordAsync(CliCommand command, CancellationToken cancellationToken)
    {
        var startResult = _directRecordingService.Start(new DirectRecordingRequest(
            command.MonitorName!,
            command.RedactionRegions ?? Array.Empty<PixelBounds>(),
            command.FramesPerSecond));

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
}
