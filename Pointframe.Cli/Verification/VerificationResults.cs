namespace Pointframe.Cli;

internal static class VerificationStatus
{
    internal const string Pass = "pass";
    internal const string Fail = "fail";
    internal const string Partial = "partial";
    internal const string Skipped = "skipped";
}

internal sealed record VerificationStepResult(
    int Index,
    string Kind,
    string Status,
    string Description,
    string? Code = null,
    string? Message = null,
    string? Expected = null,
    string? Actual = null);

internal sealed record VerificationCriterionResult(string Id, string Text, string Verdict);

internal sealed record VerificationScenarioResult(
    string Id,
    string Status,
    string? ReportVerdict,
    IReadOnlyList<VerificationCriterionResult> Criteria,
    IReadOnlyList<VerificationStepResult> Steps,
    string? BundleDirectory,
    bool ProofValid,
    string? ProofKeyId,
    IReadOnlyList<string> Problems);

// What a reviewer needs to tie the verdict to the inputs: the source tree it ran on, the spec, and the
// verifier and server that produced it. The signature in each bundle covers only that bundle.
internal sealed record VerificationProvenance(
    string? Head,
    string? TreeHash,
    string? VerifierVersion,
    string? McpExecutablePath,
    string? McpSha256,
    string? CommandsApprovedBy = null);

internal sealed record VerificationTaskInfo(
    string Id,
    string SnapshotSha256,
    DateTimeOffset CreatedUtc,
    string? TreeHashAtStart,
    bool SpecChanged,
    IReadOnlyList<string> SpecChanges,
    IReadOnlyList<string> RequiredAutomationIds);

internal sealed record VerificationVerdict(
    int SchemaVersion,
    string Status,
    bool Complete,
    DateTimeOffset StartedUtc,
    double Seconds,
    string SpecPath,
    string? SpecSha256,
    VerificationProvenance Provenance,
    string? Only,
    string? ScenarioFilter,
    VerificationTaskInfo? Task,
    IReadOnlyList<VerificationGateResult> Gates,
    IReadOnlyList<VerificationScenarioResult> Scenarios,
    string? ErrorCode = null,
    string? Error = null,
    IReadOnlyList<string>? Details = null,
    VerificationNextStep? NextStep = null,
    IReadOnlyList<string>? Warnings = null,
    bool? NoBuildGate = null);
