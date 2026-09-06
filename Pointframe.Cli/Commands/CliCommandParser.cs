namespace Pointframe.Cli;

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
