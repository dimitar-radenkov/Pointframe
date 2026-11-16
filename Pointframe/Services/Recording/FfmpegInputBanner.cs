using System.Globalization;
using System.Text.RegularExpressions;

namespace Pointframe.Services;

// Reads what ffmpeg prints about its inputs. With -use_wallclock_as_timestamps the rawvideo input's start is epoch seconds.
internal static partial class FfmpegInputBanner
{
    // Offsets outside this range are not a plausible startup delay, so the measurement is ignored.
    internal static readonly TimeSpan MinOffset = TimeSpan.FromSeconds(-2);
    internal static readonly TimeSpan MaxOffset = TimeSpan.FromSeconds(30);
    private const double MinEpochSeconds = 1_000_000_000d;

    [GeneratedRegex(@"^\s*Input #(?<index>\d+),")]
    private static partial Regex InputHeader();

    [GeneratedRegex(@"\bstart:\s*(?<start>-?\d+(?:\.\d+)?)")]
    private static partial Regex StartField();

    public static bool IsInputHeader(string line, out int index)
    {
        var match = InputHeader().Match(line);
        index = match.Success ? int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture) : -1;
        return match.Success;
    }

    public static double? TryParseStartEpoch(string line)
    {
        var match = StartField().Match(line);
        if (!match.Success
            || !double.TryParse(match.Groups["start"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var start)
            || start < MinEpochSeconds)
        {
            return null;
        }

        return start;
    }

    // How much later ffmpeg's video timeline started than the first frame the recorder captured. Null when unknown or implausible.
    public static TimeSpan? ComputeVideoStartOffset(DateTimeOffset firstFrameCapturedUtc, double? ffmpegStartEpochSeconds)
    {
        if (ffmpegStartEpochSeconds is not { } start)
        {
            return null;
        }

        var captured = firstFrameCapturedUtc.ToUnixTimeMilliseconds() / 1000d;
        var offset = TimeSpan.FromSeconds(start - captured);
        return offset < MinOffset || offset > MaxOffset ? null : offset;
    }
}
