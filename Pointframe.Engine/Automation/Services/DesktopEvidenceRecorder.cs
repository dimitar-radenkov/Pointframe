using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Pointframe.Engine.Automation.Models;

namespace Pointframe.Engine.Automation.Services;

public enum DesktopEvidenceMode
{
    None,
    Failures,
    All,
}

public sealed record DesktopTestEvidence(
    string? Path,
    string? Sha256,
    PixelBounds? BoundsPixels,
    DateTimeOffset CapturedUtc,
    string? Error = null);

public interface IDesktopEvidenceRecorder
{
    string? BeginSession(string sessionRef, string artifactRoot, DesktopEvidenceMode mode);

    bool ShouldCapture(string sessionRef, bool isFailure);

    DesktopTestEvidence Capture(string sessionRef, DesktopProcessIdentity process, string kind);
}

// Evidence is what the server saw, saved where the agent cannot rewrite the report's view of it: each
// image is referenced by its SHA-256, so a changed file no longer matches the report.
public sealed class DesktopEvidenceRecorder(
    IDisplayCaptureEngine captureEngine,
    IWindowDiscoveryService windowDiscovery,
    TimeProvider? timeProvider = null) : IDesktopEvidenceRecorder
{
    private readonly Dictionary<string, SessionEvidence> _sessions = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public string? BeginSession(string sessionRef, string artifactRoot, DesktopEvidenceMode mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactRoot);
        var directory = mode == DesktopEvidenceMode.None
            ? null
            : System.IO.Path.Combine(artifactRoot, sessionRef, "evidence");
        lock (_sync)
        {
            _sessions[sessionRef] = new SessionEvidence(directory, mode);
        }

        return directory;
    }

    public bool ShouldCapture(string sessionRef, bool isFailure)
    {
        lock (_sync)
        {
            return _sessions.TryGetValue(sessionRef, out var session)
                && (session.Mode == DesktopEvidenceMode.All || (session.Mode == DesktopEvidenceMode.Failures && isFailure));
        }
    }

    public DesktopTestEvidence Capture(string sessionRef, DesktopProcessIdentity process, string kind)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        var capturedUtc = _timeProvider.GetUtcNow();
        string directory;
        int sequence;
        lock (_sync)
        {
            if (!_sessions.TryGetValue(sessionRef, out var session) || session.Directory is null)
            {
                return new DesktopTestEvidence(null, null, null, capturedUtc, "EvidenceDisabled");
            }

            directory = session.Directory;
            sequence = ++session.Sequence;
        }

        // Only the target's own windows are captured. A whole-monitor capture would store whatever else
        // the user had open, which the policy never approved as evidence.
        if (TargetBounds(process.ProcessId) is not { } bounds)
        {
            return new DesktopTestEvidence(null, null, null, capturedUtc, "NoVisibleWindow");
        }

        // Capture failures must not fail the action or check they document; they are recorded instead,
        // so a reviewer sees that evidence is missing rather than finding nothing.
        try
        {
            using var bitmap = captureEngine.Capture(bounds);
            using var png = new MemoryStream();
            bitmap.Save(png, ImageFormat.Png);
            var bytes = png.ToArray();
            var fileName = $"{sequence:D4}-{kind}.png";
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(System.IO.Path.Combine(directory, fileName), bytes);
            return new DesktopTestEvidence(fileName, Convert.ToHexString(SHA256.HashData(bytes)), bounds, capturedUtc);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ExternalException or ArgumentException or InvalidOperationException)
        {
            return new DesktopTestEvidence(null, null, bounds, capturedUtc, "CaptureFailed");
        }
    }

    private PixelBounds? TargetBounds(int processId)
    {
        var windows = windowDiscovery.GetWindows()
            .Where(window => window.ProcessId == processId
                && !window.IsMinimized
                && window.BoundsPixels.Width > 0
                && window.BoundsPixels.Height > 0)
            .Select(window => window.BoundsPixels)
            .ToArray();
        if (windows.Length == 0)
        {
            return null;
        }

        var left = windows.Min(window => window.X);
        var top = windows.Min(window => window.Y);
        var right = windows.Max(window => window.X + window.Width);
        var bottom = windows.Max(window => window.Y + window.Height);
        return new PixelBounds(left, top, right - left, bottom - top);
    }

    private sealed class SessionEvidence(string? directory, DesktopEvidenceMode mode)
    {
        public string? Directory { get; } = directory;
        public DesktopEvidenceMode Mode { get; } = mode;
        public int Sequence { get; set; }
    }
}
