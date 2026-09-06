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

internal static class CliCommandParser
{
    internal const string Usage = "Usage: pointframe-cli displays | capture --monitor <exact Windows device name>";

    internal static bool TryParse(string[] args, out CliCommand command, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 1 && string.Equals(args[0], "displays", StringComparison.OrdinalIgnoreCase))
        {
            command = new CliCommand("displays");
            error = null;
            return true;
        }

        if (args.Length == 3
            && string.Equals(args[0], "capture", StringComparison.OrdinalIgnoreCase)
            && string.Equals(args[1], "--monitor", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(args[2]))
        {
            command = new CliCommand("capture", args[2]);
            error = null;
            return true;
        }

        command = default!;
        error = args.FirstOrDefault()?.Equals("capture", StringComparison.OrdinalIgnoreCase) == true
            ? "The capture command requires --monitor followed by an exact Windows device name."
            : "Unknown or incomplete command.";
        return false;
    }
}

internal sealed record CliCommand(string Name, string? MonitorName = null);
