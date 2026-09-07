using System.Text.Json;
using Pointframe.Engine.Automation.Models;

namespace Pointframe.Engine.Automation.Services;

public sealed record DesktopTestActionReport(
    string ActionId,
    string Description,
    DesktopActionResult Result,
    DateTimeOffset RecordedUtc);

public sealed record DesktopTestCheckReport(
    string Description,
    DesktopVerificationStatus Verdict,
    bool External,
    string? OracleType,
    string? Message,
    DateTimeOffset RecordedUtc);

public sealed record DesktopTestReport(
    int SchemaVersion,
    string SessionRef,
    string? ExecutablePath,
    string? ExecutableSha256,
    DateTimeOffset StartedUtc,
    DateTimeOffset? CompletedUtc,
    IReadOnlyList<DesktopTestActionReport> Actions,
    IReadOnlyList<DesktopTestCheckReport> Checks,
    string Verdict);

public interface IDesktopTestReportService
{
    void Initialize(string sessionRef, string executablePath, string executableSha256);

    void RecordAction(string sessionRef, DesktopTestActionReport action);

    void RecordCheck(string sessionRef, DesktopTestCheckReport check);

    DesktopTestReport Get(string sessionRef);

    Task<DesktopTestReport> FinalizeAsync(string sessionRef, CancellationToken cancellationToken = default);
}

public sealed class DesktopTestReportService(TimeProvider? timeProvider = null) : IDesktopTestReportService
{
    private readonly Dictionary<string, MutableReport> _reports = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public void RecordAction(string sessionRef, DesktopTestActionReport action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_sync)
        {
            GetOrCreate(sessionRef).Actions.Add(action);
        }
    }

    public void RecordCheck(string sessionRef, DesktopTestCheckReport check)
    {
        ArgumentNullException.ThrowIfNull(check);
        lock (_sync)
        {
            GetOrCreate(sessionRef).Checks.Add(check);
        }
    }

    public void Initialize(string sessionRef, string executablePath, string executableSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(executableSha256);
        lock (_sync)
        {
            var report = GetOrCreate(sessionRef);
            report.ExecutablePath = executablePath;
            report.ExecutableSha256 = executableSha256;
        }
    }

    public DesktopTestReport Get(string sessionRef)
    {
        var report = GetOrCreate(sessionRef);
        lock (_sync)
        {
            return report.ToImmutable();
        }
    }

    public Task<DesktopTestReport> FinalizeAsync(string sessionRef, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var report = GetOrCreate(sessionRef);
        lock (_sync)
        {
            report.CompletedUtc ??= _timeProvider.GetUtcNow();
            report.Verdict = report.Checks.Any(check => check.Verdict == DesktopVerificationStatus.Failed)
                ? "failed"
                : report.Checks.Any(check => check.Verdict == DesktopVerificationStatus.Inconclusive)
                    ? "inconclusive"
                    : "passed";
            return Task.FromResult(report.ToImmutable());
        }
    }

    public static async Task WriteAsync(
        DesktopTestReport report,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken).ConfigureAwait(false);
    }

    private MutableReport GetOrCreate(string sessionRef)
    {
        if (string.IsNullOrWhiteSpace(sessionRef))
        {
            throw new ArgumentException("A session reference is required.", nameof(sessionRef));
        }

        lock (_sync)
        {
            if (!_reports.TryGetValue(sessionRef, out var report))
            {
                report = new MutableReport(sessionRef, _timeProvider.GetUtcNow());
                _reports.Add(sessionRef, report);
            }

            return report;
        }
    }

    private sealed class MutableReport(string sessionRef, DateTimeOffset startedUtc)
    {
        public string SessionRef { get; } = sessionRef;
        public string? ExecutablePath { get; set; }
        public string? ExecutableSha256 { get; set; }
        public DateTimeOffset StartedUtc { get; } = startedUtc;
        public DateTimeOffset? CompletedUtc { get; set; }
        public List<DesktopTestActionReport> Actions { get; } = [];
        public List<DesktopTestCheckReport> Checks { get; } = [];
        public string Verdict { get; set; } = "inconclusive";

        public DesktopTestReport ToImmutable()
        {
            return new DesktopTestReport(
                DesktopTestingLimits.SchemaVersion,
                SessionRef,
                ExecutablePath,
                ExecutableSha256,
                StartedUtc,
                CompletedUtc,
                Actions.ToArray(),
                Checks.ToArray(),
                Verdict);
        }
    }
}
