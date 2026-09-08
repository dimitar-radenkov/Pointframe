using Pointframe.Engine;

namespace Pointframe.Cli;

internal static class CliCommandParser
{
    internal const string Usage = "Usage: Pointframe.Cli.exe displays | capture --monitor <exact Windows device name> | ocr --monitor <exact Windows device name> | record --monitor <exact Windows device name> --seconds <positive integer> [--fps <1-60>] [--redact <x,y,width,height>]...";

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

        if (args.Length == 3
            && string.Equals(args[0], "ocr", StringComparison.OrdinalIgnoreCase)
            && string.Equals(args[1], "--monitor", StringComparison.OrdinalIgnoreCase)
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

            if (string.Equals(flag, "--monitor", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                monitorName = args[index + 1];
                index += 2;
                continue;
            }

            if (string.Equals(flag, "--seconds", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                if (!int.TryParse(args[index + 1], out var parsedSeconds) || parsedSeconds <= 0)
                {
                    command = default!;
                    error = "The record command requires --seconds to be a positive integer.";
                    return false;
                }

                seconds = parsedSeconds;
                index += 2;
                continue;
            }

            if (string.Equals(flag, "--fps", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                if (!int.TryParse(args[index + 1], out framesPerSecond))
                {
                    command = default!;
                    error = "The record command requires --fps to be an integer.";
                    return false;
                }

                index += 2;
                continue;
            }

            if (string.Equals(flag, "--redact", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                if (!TryParseRedactionRegion(args[index + 1], out var region))
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
}
