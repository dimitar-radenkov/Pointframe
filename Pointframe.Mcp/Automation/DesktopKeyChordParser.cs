using System.Globalization;
using Pointframe.Engine.Automation.Models;

namespace Pointframe.Mcp.Automation;

internal sealed record DesktopChordParseResult(IReadOnlyList<ushort>? VirtualKeys, string? ErrorCode, string? ErrorMessage)
{
    public bool IsValid => ErrorCode is null;
}

// Turns a named chord such as "Ctrl+S" into the Windows virtual-key codes the existing press path already
// validates and dispatches. Modifiers come first and in a fixed order, then the one key.
internal static class DesktopKeyChordParser
{
    private const ushort Shift = 0x10;
    private const ushort Control = 0x11;
    private const ushort Alt = 0x12;
    private const ushort Windows = 0x5B;

    private static readonly Dictionary<string, ushort> Modifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = Control,
        ["Control"] = Control,
        ["Shift"] = Shift,
        ["Alt"] = Alt,
        ["Win"] = Windows,
        ["Windows"] = Windows,
    };

    private static readonly Dictionary<string, ushort> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Enter"] = 0x0D,
        ["Return"] = 0x0D,
        ["Esc"] = 0x1B,
        ["Escape"] = 0x1B,
        ["Tab"] = 0x09,
        ["Space"] = 0x20,
        ["Backspace"] = 0x08,
        ["Delete"] = 0x2E,
        ["Del"] = 0x2E,
        ["Home"] = 0x24,
        ["End"] = 0x23,
        ["PageUp"] = 0x21,
        ["PageDown"] = 0x22,
        ["Left"] = 0x25,
        ["Up"] = 0x26,
        ["Right"] = 0x27,
        ["Down"] = 0x28,
    };

    private static readonly ushort[] ModifierOrder = [Control, Shift, Alt, Windows];

    public static DesktopChordParseResult Parse(string? chord)
    {
        if (string.IsNullOrWhiteSpace(chord))
        {
            return Invalid("InvalidRequest", "A chord is required, for example \"Ctrl+S\".");
        }

        var tokens = chord.Split('+', StringSplitOptions.TrimEntries);
        if (tokens.Any(string.IsNullOrEmpty))
        {
            return Invalid("InvalidRequest", $"The chord '{chord}' has an empty part. Join modifiers and one key with '+', for example \"Ctrl+Shift+S\".");
        }

        var modifiers = new HashSet<ushort>();
        ushort? key = null;
        foreach (var token in tokens)
        {
            if (Modifiers.TryGetValue(token, out var modifier))
            {
                if (!modifiers.Add(modifier))
                {
                    return Invalid("InvalidRequest", $"The chord '{chord}' repeats the modifier '{token}'.");
                }

                continue;
            }

            if (!TryResolveKey(token, out var resolved))
            {
                return Invalid("InvalidRequest", $"Unknown key '{token}' in chord '{chord}'. Use Ctrl, Shift, Alt or Win plus one letter, digit, F1-F24, Enter, Esc, Tab, Space, Backspace, Delete, Home, End, PageUp, PageDown or an arrow key.");
            }

            if (key is not null)
            {
                return Invalid("InvalidRequest", $"The chord '{chord}' names more than one non-modifier key.");
            }

            key = resolved;
        }

        if (key is null)
        {
            return Invalid("InvalidRequest", $"The chord '{chord}' has only modifiers. Add one key, for example \"{chord}+S\".");
        }

        var keys = ModifierOrder.Where(modifiers.Contains).Append(key.Value).ToArray();
        if (keys.Length > DesktopTestingLimits.MaxSimultaneousKeys)
        {
            return Invalid("InvalidKeyCount", "A key press must contain one to four keys.");
        }

        return new DesktopChordParseResult(keys, null, null);
    }

    private static bool TryResolveKey(string token, out ushort key)
    {
        if (NamedKeys.TryGetValue(token, out key))
        {
            return true;
        }

        if (token.Length == 1 && char.IsAsciiLetter(token[0]))
        {
            key = (ushort)char.ToUpperInvariant(token[0]);
            return true;
        }

        if (token.Length == 1 && char.IsAsciiDigit(token[0]))
        {
            key = token[0];
            return true;
        }

        if (token.Length is 2 or 3
            && (token[0] == 'F' || token[0] == 'f')
            && int.TryParse(token.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number is >= 1 and <= 24)
        {
            key = (ushort)(0x70 + number - 1);
            return true;
        }

        key = 0;
        return false;
    }

    private static DesktopChordParseResult Invalid(string code, string message) => new(null, code, message);
}
