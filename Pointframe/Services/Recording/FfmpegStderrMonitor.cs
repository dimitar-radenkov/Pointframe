using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Pointframe.Services;

internal sealed partial class FfmpegStderrMonitor
{
    private const int RecentLineLimit = 12;

    private readonly ILogger _logger;
    private readonly ConcurrentQueue<string> _recentLines = new();
    private readonly TaskCompletionSource<bool> _firstInputOpened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _expectFirstInputStart;
    private readonly object _startLock = new();
    private double? _firstInputStartSeconds;

    public FfmpegStderrMonitor(ILogger logger)
    {
        _logger = logger;
    }

    public string RecentLines => string.Join(Environment.NewLine, _recentLines);

    // Completes true when ffmpeg reports its first input opened, false when stderr ends before that.
    public Task<bool> FirstInputOpened => _firstInputOpened.Task;

    // dshow stamps audio with QueryPerformanceCounter seconds since boot; ffmpeg prints that origin as "start:" on the
    // Duration line right after "Input #0". Null when the line is missing or says N/A.
    public double? FirstInputStartSeconds
    {
        get
        {
            lock (_startLock)
            {
                return _firstInputStartSeconds;
            }
        }
    }

    internal static double? ParseStartSeconds(string line)
    {
        var match = StartRegex().Match(line);
        return match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? seconds
            : null;
    }

    [GeneratedRegex(@"^\s*Duration:.*?start:\s*(-?\d+(?:\.\d+)?)")]
    private static partial Regex StartRegex();

    // ffmpeg prints "Input #<n>, <demuxer>, from '<url>':" after it has opened and probed input n. Inputs open in
    // command order, and a rawvideo pipe input is probed only once its first frame arrives, so with the microphone
    // listed first, "Input #0" means the microphone device is open and ffmpeg waits for the first video frame.
    internal static bool IsFirstInputOpenedLine(string line)
    {
        return line.StartsWith("Input #0,", StringComparison.Ordinal);
    }

    public async Task RunAsync(TextReader reader)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                Observe(line);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read ffmpeg stderr");
        }
        finally
        {
            _firstInputOpened.TrySetResult(false);
        }
    }

    internal void Observe(string line)
    {
        _recentLines.Enqueue(line);
        while (_recentLines.Count > RecentLineLimit)
        {
            _recentLines.TryDequeue(out _);
        }

        if (_expectFirstInputStart && line.TrimStart().StartsWith("Duration:", StringComparison.Ordinal))
        {
            _expectFirstInputStart = false;
            lock (_startLock)
            {
                _firstInputStartSeconds = ParseStartSeconds(line);
            }
        }

        if (IsFirstInputOpenedLine(line))
        {
            _expectFirstInputStart = true;
            _firstInputOpened.TrySetResult(true);
        }
        else if (line.StartsWith("Input #", StringComparison.Ordinal))
        {
            _expectFirstInputStart = false;
        }

        if (line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("warning", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("ffmpeg: {Line}", line);
        }
        else
        {
            _logger.LogDebug("ffmpeg: {Line}", line);
        }
    }
}
