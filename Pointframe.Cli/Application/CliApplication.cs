using Pointframe.Engine;

namespace Pointframe.Cli;

internal sealed class CliApplication
{
    private readonly IDirectCaptureService _directCaptureService;
    private readonly TextWriter _standardOutput;
    private readonly TextWriter _standardError;

    internal CliApplication(IDirectCaptureService directCaptureService, TextWriter standardOutput, TextWriter standardError)
    {
        _directCaptureService = directCaptureService;
        _standardOutput = standardOutput;
        _standardError = standardError;
    }

    internal static async Task<int> RunAsync(string[] args, TextWriter standardOutput, TextWriter standardError)
    {
        try
        {
            var directCaptureService = new DirectCaptureService(new DisplayCaptureEngine());
            return await new CliApplication(directCaptureService, standardOutput, standardError).RunAsync(args);
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
            var payload = command.Name switch
            {
                "displays" => _directCaptureService.ListDisplays(),
                "capture" => await _directCaptureService.CaptureMonitorAsync(command.MonitorName!, cancellationToken),
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
}
