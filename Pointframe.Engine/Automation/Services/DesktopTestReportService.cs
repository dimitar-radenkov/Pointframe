using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pointframe.Engine.Automation.Models;

namespace Pointframe.Engine.Automation.Services;

public sealed record DesktopTestActionReport(
    string ActionId,
    string Description,
    DesktopActionResult Result,
    DateTimeOffset RecordedUtc,
    DesktopTestEvidence? Evidence = null);

public sealed record DesktopTestCheckReport(
    string Description,
    DesktopVerificationStatus Verdict,
    bool External,
    string? OracleType,
    string? Message,
    DateTimeOffset RecordedUtc,
    string? CriterionId = null,
    bool NegativeControl = false,
    DesktopTestEvidence? Evidence = null);

public sealed record DesktopTestCriterion(
    string Id,
    string Text,
    string Verdict);

public sealed record DesktopTestReport(
    int SchemaVersion,
    string SessionRef,
    string? ExecutablePath,
    string? ExecutableSha256,
    DateTimeOffset StartedUtc,
    DateTimeOffset? CompletedUtc,
    IReadOnlyList<DesktopTestActionReport> Actions,
    IReadOnlyList<DesktopTestCheckReport> Checks,
    string Verdict,
    IReadOnlyList<DesktopTestCriterion> Criteria,
    string? CriteriaSha256,
    string? EvidenceDirectory,
    DesktopProof? Proof = null);

public interface IDesktopTestReportService
{
    void Initialize(string sessionRef, string executablePath, string executableSha256, IReadOnlyList<string>? criteria = null, string? evidenceDirectory = null);

    bool NeedsActionEvidence(string sessionRef, string actionId);

    void AttachActionEvidence(string sessionRef, string actionId, DesktopTestEvidence evidence);

    bool HasCriterion(string sessionRef, string criterionId);

    void RecordAction(string sessionRef, DesktopTestActionReport action);

    void RecordCheck(string sessionRef, DesktopTestCheckReport check);

    DesktopTestReport Get(string sessionRef);

    Task<DesktopTestReport> FinalizeAsync(string sessionRef, CancellationToken cancellationToken = default);
}

public sealed class DesktopTestReportService(TimeProvider? timeProvider = null, IDesktopProofSigner? signer = null) : IDesktopTestReportService
{
    public const int MaxCriteria = 50;
    public const int MaxCriterionLength = 500;

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

    public void Initialize(string sessionRef, string executablePath, string executableSha256, IReadOnlyList<string>? criteria = null, string? evidenceDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(executableSha256);
        var normalized = NormalizeCriteria(criteria);
        lock (_sync)
        {
            var report = GetOrCreate(sessionRef);
            if (report.Criteria.Count > 0)
            {
                // Criteria are the contract the session is judged against. Replacing them after work has
                // started would let the target move to wherever the work landed.
                throw new InvalidOperationException("The session's acceptance criteria are already frozen.");
            }

            report.ExecutablePath = executablePath;
            report.ExecutableSha256 = executableSha256;
            report.Criteria.AddRange(normalized.Select((text, index) => ($"C{index + 1}", text)));
            report.CriteriaSha256 = normalized.Count == 0 ? null : HashCriteria(normalized);
            report.EvidenceDirectory = evidenceDirectory;
        }
    }

    public bool HasCriterion(string sessionRef, string criterionId)
    {
        var report = GetOrCreate(sessionRef);
        lock (_sync)
        {
            return report.Criteria.Any(criterion => string.Equals(criterion.Id, criterionId, StringComparison.Ordinal));
        }
    }

    // A replayed action ID returns its stored result without a new record, so only the first record of an
    // action ever needs a capture.
    public bool NeedsActionEvidence(string sessionRef, string actionId)
    {
        var report = GetOrCreate(sessionRef);
        lock (_sync)
        {
            return report.Actions.FindLastIndex(action => action.ActionId == actionId && action.Evidence is null) >= 0;
        }
    }

    public void AttachActionEvidence(string sessionRef, string actionId, DesktopTestEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var report = GetOrCreate(sessionRef);
        lock (_sync)
        {
            var index = report.Actions.FindLastIndex(action => action.ActionId == actionId && action.Evidence is null);
            if (index >= 0)
            {
                report.Actions[index] = report.Actions[index] with { Evidence = evidence };
            }
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
        DesktopTestReport finalized;
        lock (_sync)
        {
            report.CompletedUtc ??= _timeProvider.GetUtcNow();
            report.Verdict = ComputeVerdict(report.Actions, report.Checks, report.CriterionVerdicts());
            finalized = report.ToImmutable();
        }

        // Sealed on every finalize, so a report fetched again after more steps covers those steps too.
        return Task.FromResult(signer is null ? finalized : DesktopProofService.Seal(finalized, signer));
    }

    public static IReadOnlyList<string> NormalizeCriteria(IReadOnlyList<string>? criteria)
    {
        if (criteria is null || criteria.Count == 0)
        {
            return [];
        }

        if (criteria.Count > MaxCriteria)
        {
            throw new ArgumentException($"At most {MaxCriteria} acceptance criteria are allowed.", nameof(criteria));
        }

        var normalized = criteria.Select(criterion => criterion?.Trim() ?? string.Empty).ToArray();
        if (normalized.Any(string.IsNullOrEmpty))
        {
            throw new ArgumentException("Acceptance criteria cannot be empty.", nameof(criteria));
        }

        if (normalized.Any(criterion => criterion.Length > MaxCriterionLength))
        {
            throw new ArgumentException($"An acceptance criterion can be at most {MaxCriterionLength} characters.", nameof(criteria));
        }

        return normalized;
    }

    public static string HashCriteria(IReadOnlyList<string> criteria) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(criteria))));

    public static string ComputeVerdict(
        IReadOnlyList<DesktopTestActionReport> actions,
        IReadOnlyList<DesktopTestCheckReport> checks,
        IReadOnlyList<DesktopTestCriterion> criteria)
    {
        // An action rejected before dispatch never touched the application, so the agent may retry it
        // without poisoning the report. Once input was dispatched, a failed verification is a failure, and
        // an uncertain dispatch means the final state cannot be attributed to the recorded steps.
        var dispatched = actions.Where(action => action.Result.Dispatch != DesktopDispatchStatus.NotStarted).ToArray();
        if (checks.Any(check => check.Verdict == DesktopVerificationStatus.Failed)
            || dispatched.Any(action => action.Result.Verification == DesktopVerificationStatus.Failed))
        {
            return "failed";
        }

        // A run that checked nothing has shown nothing, whatever its actions reported. Negative controls
        // only show that the oracle can tell states apart, so they do not count as checking the work.
        if (!checks.Any(check => !check.NegativeControl)
            || checks.Any(check => check.Verdict == DesktopVerificationStatus.Inconclusive)
            || dispatched.Any(action => action.Result.Dispatch is DesktopDispatchStatus.Partial or DesktopDispatchStatus.Unknown)
            || criteria.Any(criterion => criterion.Verdict != "passed"))
        {
            return "inconclusive";
        }

        // Passing criteria is evidence only if the same session showed the oracle rejecting a wrong
        // expectation; an oracle that matches everything would pass every criterion too.
        if (criteria.Count > 0
            && !checks.Any(check => check.NegativeControl && check.Verdict == DesktopVerificationStatus.Passed))
        {
            return "inconclusive";
        }

        return "passed";
    }

    public static string ComputeCriterionVerdict(string criterionId, IReadOnlyList<DesktopTestCheckReport> checks)
    {
        var covering = checks
            .Where(check => !check.NegativeControl && string.Equals(check.CriterionId, criterionId, StringComparison.Ordinal))
            .ToArray();
        if (covering.Any(check => check.Verdict == DesktopVerificationStatus.Failed))
        {
            return "failed";
        }

        if (covering.Any(check => check.Verdict == DesktopVerificationStatus.Passed))
        {
            return "passed";
        }

        return covering.Length == 0 ? "uncovered" : "inconclusive";
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
        public List<(string Id, string Text)> Criteria { get; } = [];
        public string? CriteriaSha256 { get; set; }
        public string? EvidenceDirectory { get; set; }
        public string Verdict { get; set; } = "inconclusive";

        public DesktopTestCriterion[] CriterionVerdicts() =>
            Criteria.Select(criterion => new DesktopTestCriterion(
                criterion.Id,
                criterion.Text,
                ComputeCriterionVerdict(criterion.Id, Checks))).ToArray();

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
                Verdict,
                CriterionVerdicts(),
                CriteriaSha256,
                EvidenceDirectory);
        }
    }
}
