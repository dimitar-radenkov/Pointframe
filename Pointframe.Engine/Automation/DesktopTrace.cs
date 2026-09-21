namespace Pointframe.Engine.Automation;

/// <summary>
/// Diagnostic tracing for the desktop driver. Writes to the file named by POINTFRAME_TRACE and is a
/// no-op when that variable is unset. It deliberately never writes to standard output or standard
/// error: the MCP server speaks JSON-RPC over those streams, so a stray line there corrupts the
/// protocol rather than diagnosing it.
/// </summary>
public static class DesktopTrace
{
    private static readonly string? Path = Environment.GetEnvironmentVariable("POINTFRAME_TRACE");
    private static readonly object Sync = new();

    public static bool Enabled => !string.IsNullOrWhiteSpace(Path);

    public static void Write(string message)
    {
        if (!Enabled)
        {
            return;
        }

        var line = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{DateTimeOffset.UtcNow:HH:mm:ss.fff} pid={Environment.ProcessId} tid={Environment.CurrentManagedThreadId} {message}{Environment.NewLine}");
        try
        {
            lock (Sync)
            {
                File.AppendAllText(Path!, line);
            }
        }
        catch (IOException)
        {
            // Tracing must never take the process down.
        }
    }

    public static IDisposable Scope(string name)
    {
        Write($"-> {name}");
        return new Span(name);
    }

    private sealed class Span(string name) : IDisposable
    {
        private readonly System.Diagnostics.Stopwatch _watch = System.Diagnostics.Stopwatch.StartNew();

        public void Dispose()
        {
            _watch.Stop();
            Write($"<- {name} ({_watch.ElapsedMilliseconds} ms)");
        }
    }
}
