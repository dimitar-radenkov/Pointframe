using Pointframe.Engine;

namespace Pointframe.Cli;

internal static class CliCommandParser
{
    internal const string Usage = "Usage: Pointframe.Cli.exe displays | windows | capture --monitor <exact Windows device name> [--region <x,y,width,height>] [--output <file>] | ocr --monitor <exact Windows device name> [--region <x,y,width,height>] [--output <file>] | capture-window --window-id <id> [--output <file>] | ocr-window --window-id <id> [--output <file>] | record --monitor <exact Windows device name> --seconds <positive integer> [--fps <1-60>] [--redact <x,y,width,height>]... [--output <file>] | --help | --version";

    internal const string HelpText = """
        Pointframe CLI - standalone screen capture, OCR, and recording automation.

        Usage:
          Pointframe.Cli.exe displays
          Pointframe.Cli.exe windows
          Pointframe.Cli.exe capture --monitor <exact Windows device name> [--region <x,y,width,height>] [--output <file>]
          Pointframe.Cli.exe ocr --monitor <exact Windows device name> [--region <x,y,width,height>] [--output <file>]
          Pointframe.Cli.exe capture-window --window-id <id> [--output <file>]
          Pointframe.Cli.exe ocr-window --window-id <id> [--output <file>]
          Pointframe.Cli.exe record --monitor <exact Windows device name> --seconds <positive integer> [--fps <1-60>] [--redact <x,y,width,height>]... [--output <file>]
          Pointframe.Cli.exe --help
          Pointframe.Cli.exe --version

        Commands:
          displays        List every connected monitor as JSON.
          windows         List visible top-level windows as JSON (handle, title, process, bounds).
          capture         Capture one monitor, or a region of it, to a PNG file and report it as JSON.
          ocr             Capture one monitor, or a region of it, and extract on-screen text via OCR as JSON.
          capture-window  Capture the visible screen rectangle of a window by its handle.
          ocr-window      Capture a window and extract on-screen text via OCR.
          record          Record one monitor to an MP4 for a fixed duration, then exit with a JSON summary.

        Options:
          -m, --monitor <name>              Exact Windows device name (see 'displays' for exact values), e.g. \\.\DISPLAY1
          -w, --window-id <id>              Window handle returned by 'windows', e.g. 12345678
          -g, --region <x,y,width,height>   capture/ocr: monitor-local physical-pixel sub-region; captures the whole monitor when omitted
          -s, --seconds <n>                 record: capture duration in whole seconds (positive integer)
          -f, --fps <1-60>                  record: capture frame rate (default 20)
          -r, --redact <x,y,width,height>   record: pixelate a capture-local physical-pixel region; repeatable
          -o, --output <file>               Exact output file to write (.png for capture/ocr, .mp4 for record);
                                            parent directories are created. Defaults to a timestamped name
                                            under %LOCALAPPDATA%\Pointframe when omitted.
          -h, --help                        Show this help text and exit
          -v, --version                     Show the CLI version and exit

        Pressing Ctrl+C during 'record' stops the recording early and gracefully: the MP4 is finalized
        and its artifact is still written and reported in the JSON summary, not discarded.

        Window capture uses visible screen-rectangle semantics: it captures whatever is on screen
        at the window's bounds, which means occluding windows may appear in the capture. Minimized,
        zero-size, off-screen, and multi-monitor-spanning windows are rejected.

        Every long option above also accepts an inline value, e.g. --monitor=\\.\DISPLAY1.
        --help/--version take priority over any other arguments, so they can be appended to
        an otherwise invalid or incomplete command line to see usage instead of an error.

        Every command other than --help/--version writes a single-line JSON response to standard output and
        uses the process exit code to signal success (0), a runtime error (1), or a usage error (2).
        A runtime error writes a JSON response with "Success": false and an "Error" object carrying a stable
        machine-readable "Code" (target_not_found, target_not_capturable, invalid_region, canceled, or
        capture_failed) alongside the human-readable message, which is also repeated on standard error.
        A usage error (exit code 2) writes this usage text to standard error instead of JSON.
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

        if (args.Length == 1 && string.Equals(args[0], "windows", StringComparison.OrdinalIgnoreCase))
        {
            command = new CliCommand("windows");
            error = null;
            return true;
        }

        if (args.Length > 0 && string.Equals(args[0], "capture-window", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseWindowCaptureLikeCommand("capture-window", args, out command, out error);
        }

        if (args.Length > 0 && string.Equals(args[0], "ocr-window", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseWindowCaptureLikeCommand("ocr-window", args, out command, out error);
        }

        if (args.Length > 0 && string.Equals(args[0], "capture", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseCaptureLikeCommand("capture", args, out command, out error);
        }

        if (args.Length > 0 && string.Equals(args[0], "ocr", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseCaptureLikeCommand("ocr", args, out command, out error);
        }

        if (args.Length > 0 && string.Equals(args[0], "record", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseRecord(args, out command, out error);
        }

        command = default!;
        error = "Unknown or incomplete command.";
        return false;
    }

    private static bool TryParseCaptureLikeCommand(string commandName, string[] args, out CliCommand command, out string? error)
    {
        string? monitorName = null;
        CaptureRegion? region = null;
        string? outputPath = null;

        var index = 1;
        while (index < args.Length)
        {
            var flag = args[index];
            var hasValue = index + 1 < args.Length;

            if (IsMonitorFlag(flag))
            {
                if (!hasValue || string.IsNullOrWhiteSpace(args[index + 1]))
                {
                    command = default!;
                    error = $"The {commandName} command requires --monitor followed by an exact Windows device name.";
                    return false;
                }

                monitorName = args[index + 1];
                index += 2;
                continue;
            }

            if (IsRegionFlag(flag))
            {
                if (!hasValue || !TryParseCaptureRegion(args[index + 1], out var parsedRegion))
                {
                    command = default!;
                    error = "Each --region value must be four comma-separated integers formatted as x,y,width,height with a positive width and height.";
                    return false;
                }

                region = parsedRegion;
                index += 2;
                continue;
            }

            if (IsOutputFlag(flag))
            {
                if (!hasValue || string.IsNullOrWhiteSpace(args[index + 1]))
                {
                    command = default!;
                    error = $"The {commandName} command requires --output followed by a file path.";
                    return false;
                }

                outputPath = args[index + 1];
                index += 2;
                continue;
            }

            command = default!;
            error = $"Unrecognized {commandName} option '{flag}'.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(monitorName))
        {
            command = default!;
            error = $"The {commandName} command requires --monitor followed by an exact Windows device name.";
            return false;
        }

        command = new CliCommand(commandName, monitorName, Region: region, OutputPath: outputPath);
        error = null;
        return true;
    }

    private static bool TryParseWindowCaptureLikeCommand(string commandName, string[] args, out CliCommand command, out string? error)
    {
        long? windowId = null;
        string? outputPath = null;

        var index = 1;
        while (index < args.Length)
        {
            var flag = args[index];
            var hasValue = index + 1 < args.Length;

            if (IsWindowIdFlag(flag))
            {
                if (!hasValue || !long.TryParse(args[index + 1], out var parsedId) || parsedId <= 0)
                {
                    command = default!;
                    error = $"The {commandName} command requires --window-id followed by a positive integer window handle (see 'windows' for available handles).";
                    return false;
                }

                windowId = parsedId;
                index += 2;
                continue;
            }

            if (IsOutputFlag(flag))
            {
                if (!hasValue || string.IsNullOrWhiteSpace(args[index + 1]))
                {
                    command = default!;
                    error = $"The {commandName} command requires --output followed by a file path.";
                    return false;
                }

                outputPath = args[index + 1];
                index += 2;
                continue;
            }

            command = default!;
            error = $"Unrecognized {commandName} option '{flag}'.";
            return false;
        }

        if (windowId is null)
        {
            command = default!;
            error = $"The {commandName} command requires --window-id followed by a window handle from the 'windows' command.";
            return false;
        }

        command = new CliCommand(commandName, WindowId: windowId, OutputPath: outputPath);
        error = null;
        return true;
    }

    private static bool TryParseRecord(string[] args, out CliCommand command, out string? error)
    {
        string? monitorName = null;
        int? seconds = null;
        var framesPerSecond = 20;
        var redactionRegions = new List<PixelBounds>();
        string? outputPath = null;

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

            if (IsOutputFlag(flag))
            {
                if (!hasValue || string.IsNullOrWhiteSpace(args[index + 1]))
                {
                    command = default!;
                    error = "The record command requires --output followed by a file path.";
                    return false;
                }

                outputPath = args[index + 1];
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

        command = new CliCommand("record", monitorName, RecordSeconds: seconds, FramesPerSecond: framesPerSecond, RedactionRegions: redactionRegions, OutputPath: outputPath);
        error = null;
        return true;
    }

    private static bool TryParseRedactionRegion(string value, out PixelBounds region)
    {
        if (TryParseFourPositiveIntegers(value, out var x, out var y, out var width, out var height))
        {
            region = new PixelBounds(x, y, width, height);
            return true;
        }

        region = default;
        return false;
    }

    private static bool TryParseCaptureRegion(string value, out CaptureRegion region)
    {
        if (TryParseFourPositiveIntegers(value, out var x, out var y, out var width, out var height))
        {
            region = new CaptureRegion(x, y, width, height);
            return true;
        }

        region = default;
        return false;
    }

    private static bool TryParseFourPositiveIntegers(string value, out int x, out int y, out int width, out int height)
    {
        var parts = value.Split(',');
        if (parts.Length == 4
            && int.TryParse(parts[0], out x)
            && int.TryParse(parts[1], out y)
            && int.TryParse(parts[2], out width)
            && int.TryParse(parts[3], out height)
            && width > 0
            && height > 0)
        {
            return true;
        }

        x = y = width = height = default;
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

    private static bool IsRegionFlag(string value) =>
        string.Equals(value, "--region", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "-g", StringComparison.OrdinalIgnoreCase);

    private static bool IsSecondsFlag(string value) =>
        string.Equals(value, "--seconds", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "-s", StringComparison.OrdinalIgnoreCase);

    private static bool IsFpsFlag(string value) =>
        string.Equals(value, "--fps", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "-f", StringComparison.OrdinalIgnoreCase);

    private static bool IsRedactFlag(string value) =>
        string.Equals(value, "--redact", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "-r", StringComparison.OrdinalIgnoreCase);

    private static bool IsOutputFlag(string value) =>
        string.Equals(value, "--output", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "-o", StringComparison.OrdinalIgnoreCase);

    private static bool IsWindowIdFlag(string value) =>
        string.Equals(value, "--window-id", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "-w", StringComparison.OrdinalIgnoreCase);
}
