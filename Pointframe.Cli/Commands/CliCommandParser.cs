using Pointframe.Engine;

namespace Pointframe.Cli;

internal static class CliCommandParser
{
    internal const string Usage = "Usage: Pointframe.Cli.exe displays | capture --monitor <exact Windows device name> | ocr --monitor <exact Windows device name> | record --monitor <exact Windows device name> --seconds <positive integer> [--fps <1-60>] [--redact <x,y,width,height>]... | --help | --version";

    internal const string HelpText = """
        Pointframe CLI - standalone screen capture, OCR, and recording automation.

        Usage:
          Pointframe.Cli.exe displays
          Pointframe.Cli.exe capture --monitor <exact Windows device name>
          Pointframe.Cli.exe ocr --monitor <exact Windows device name>
          Pointframe.Cli.exe record --monitor <exact Windows device name> --seconds <positive integer> [--fps <1-60>] [--redact <x,y,width,height>]...
          Pointframe.Cli.exe --help
          Pointframe.Cli.exe --version

        Commands:
          displays   List every connected monitor as JSON.
          capture    Capture one monitor as a PNG (base64) JSON response.
          ocr        Capture one monitor and extract on-screen text via OCR as JSON.
          record     Record one monitor to an MP4 for a fixed duration, then exit with a JSON summary.

        Options:
          -m, --monitor <name>              Exact Windows device name (see 'displays' for exact values), e.g. \\.\DISPLAY1
          -s, --seconds <n>                 record: capture duration in whole seconds (positive integer)
          -f, --fps <1-60>                  record: capture frame rate (default 20)
          -r, --redact <x,y,width,height>   record: pixelate a capture-local physical-pixel region; repeatable
          -h, --help                        Show this help text and exit
          -v, --version                     Show the CLI version and exit

        Every long option above also accepts an inline value, e.g. --monitor=\\.\DISPLAY1.
        --help/--version take priority over any other arguments, so they can be appended to
        an otherwise invalid or incomplete command line to see usage instead of an error.

        Every command other than --help/--version writes a single-line JSON response to standard output and
        uses the process exit code to signal success (0), a runtime error (1), or a usage error (2).
        """;

    internal static bool TryParse(string[] args, out CliCommand command, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);

        args = ExpandInlineFlagValues(args);

        if (args.Any(IsHelpToken))
        {
            command = new CliCommand("help");
            error = null;
            return true;
        }

        if (args.Any(IsVersionToken))
        {
            command = new CliCommand("version");
            error = null;
            return true;
        }

        if (args.Length == 1 && IsHelpFlag(args[0]))
        {
            command = new CliCommand("help");
            error = null;
            return true;
        }

        if (args.Length == 1 && IsVersionFlag(args[0]))
        {
            command = new CliCommand("version");
            error = null;
            return true;
        }

        if (args.Length == 1 && string.Equals(args[0], "displays", StringComparison.OrdinalIgnoreCase))
        {
            command = new CliCommand("displays");
            error = null;
            return true;
        }

        if (args.Length == 3
            && string.Equals(args[0], "capture", StringComparison.OrdinalIgnoreCase)
            && IsMonitorFlag(args[1])
            && !string.IsNullOrWhiteSpace(args[2]))
        {
            command = new CliCommand("capture", args[2]);
            error = null;
            return true;
        }

        if (args.Length == 3
            && string.Equals(args[0], "ocr", StringComparison.OrdinalIgnoreCase)
            && IsMonitorFlag(args[1])
            && !string.IsNullOrWhiteSpace(args[2]))
        {
            command = new CliCommand("ocr", args[2]);
            error = null;
            return true;
        }

        if (args.Length > 0 && string.Equals(args[0], "record", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseRecord(args, out command, out error);
        }

        command = default!;
        error = args.FirstOrDefault()?.Equals("capture", StringComparison.OrdinalIgnoreCase) == true
            ? "The capture command requires --monitor followed by an exact Windows device name."
            : args.FirstOrDefault()?.Equals("ocr", StringComparison.OrdinalIgnoreCase) == true
                ? "The ocr command requires --monitor followed by an exact Windows device name."
                : "Unknown or incomplete command.";
        return false;
    }

    private static bool TryParseRecord(string[] args, out CliCommand command, out string? error)
    {
        string? monitorName = null;
        int? seconds = null;
        var framesPerSecond = 20;
        var redactionRegions = new List<PixelBounds>();

        var index = 1;
        while (index < args.Length)
        {
            var flag = args[index];
            var hasValue = index + 1 < args.Length;

            if (IsMonitorFlag(flag))
            {
                if (!hasValue)
                {
                    command = default!;
                    error = "The record command requires --monitor followed by an exact Windows device name.";
                    return false;
                }

                monitorName = args[index + 1];
                index += 2;
                continue;
            }

            if (IsSecondsFlag(flag))
            {
                if (!hasValue || !int.TryParse(args[index + 1], out var parsedSeconds) || parsedSeconds <= 0)
                {
                    command = default!;
                    error = "The record command requires --seconds to be a positive integer.";
                    return false;
                }

                seconds = parsedSeconds;
                index += 2;
                continue;
            }

            if (IsFpsFlag(flag))
            {
                if (!hasValue || !int.TryParse(args[index + 1], out framesPerSecond) || framesPerSecond is < 1 or > 60)
                {
                    command = default!;
                    error = "The record command requires --fps to be an integer between 1 and 60.";
                    return false;
                }

                index += 2;
                continue;
            }

            if (IsRedactFlag(flag))
            {
                if (!hasValue || !TryParseRedactionRegion(args[index + 1], out var region))
                {
                    command = default!;
                    error = "Each --redact value must be four comma-separated integers formatted as x,y,width,height with a positive width and height.";
                    return false;
                }

                redactionRegions.Add(region);
                index += 2;
                continue;
            }

            command = default!;
            error = $"Unrecognized record option '{flag}'.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(monitorName))
        {
            command = default!;
            error = "The record command requires --monitor followed by an exact Windows device name.";
            return false;
        }

        if (seconds is null)
        {
            command = default!;
            error = "The record command requires --seconds followed by a positive integer duration.";
            return false;
        }

        command = new CliCommand("record", monitorName, seconds, framesPerSecond, redactionRegions);
        error = null;
        return true;
    }

    private static bool TryParseRedactionRegion(string value, out PixelBounds region)
    {
        var parts = value.Split(',');
        if (parts.Length == 4
            && int.TryParse(parts[0], out var x)
            && int.TryParse(parts[1], out var y)
            && int.TryParse(parts[2], out var width)
            && int.TryParse(parts[3], out var height)
            && width > 0
            && height > 0)
        {
            region = new PixelBounds(x, y, width, height);
            return true;
        }

        region = default;
        return false;
    }

    /// <summary>
    /// Splits any "--flag=value" token into separate "--flag" and "value" tokens so every existing
    /// flag-parsing branch below can keep matching on adjacent tokens without knowing about the
    /// inline-value syntax.
    /// </summary>
    private static string[] ExpandInlineFlagValues(string[] args)
    {
        var hasInlineValue = args.Any(arg => arg.StartsWith("--", StringComparison.Ordinal) && arg.IndexOf('=') > 2);
        if (!hasInlineValue)
        {
            return args;
        }

        var expanded = new List<string>(args.Length);
        foreach (var arg in args)
        {
            var equalsIndex = arg.IndexOf('=');
            if (arg.StartsWith("--", StringComparison.Ordinal) && equalsIndex > 2)
            {
                expanded.Add(arg[..equalsIndex]);
                expanded.Add(arg[(equalsIndex + 1)..]);
            }
            else
            {
                expanded.Add(arg);
            }
        }

        return [.. expanded];
    }

    private static bool IsHelpToken(string value) =>
        string.Equals(value, "--help", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "-h", StringComparison.OrdinalIgnoreCase);

    private static bool IsVersionToken(string value) =>
        string.Equals(value, "--version", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "-v", StringComparison.OrdinalIgnoreCase);

    private static bool IsHelpFlag(string value) =>
        string.Equals(value, "--help", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "-h", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "help", StringComparison.OrdinalIgnoreCase);

    private static bool IsVersionFlag(string value) =>
        string.Equals(value, "--version", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "-v", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "version", StringComparison.OrdinalIgnoreCase);

    private static bool IsMonitorFlag(string value) =>
        string.Equals(value, "--monitor", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "-m", StringComparison.OrdinalIgnoreCase);

    private static bool IsSecondsFlag(string value) =>
        string.Equals(value, "--seconds", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "-s", StringComparison.OrdinalIgnoreCase);

    private static bool IsFpsFlag(string value) =>
        string.Equals(value, "--fps", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "-f", StringComparison.OrdinalIgnoreCase);

    private static bool IsRedactFlag(string value) =>
        string.Equals(value, "--redact", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "-r", StringComparison.OrdinalIgnoreCase);
}
