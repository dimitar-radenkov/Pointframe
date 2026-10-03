using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Moq;
using Pointframe.Engine.Automation.Models;
using Pointframe.Engine.Automation.Services;
using Pointframe.Mcp;
using Pointframe.Mcp.Automation;
using Pointframe.Mcp.Configuration;
using Xunit;

namespace Pointframe.Tests.Mcp;

public sealed class DesktopReplayTests : IDisposable
{
    private const string SessionId = "session-replay";
    private const string ExecutableHash = "ABC";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pointframe-replay-{Guid.NewGuid():N}");
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly Mock<IDesktopUiCheckService> _checks = new();
    private readonly DesktopTestReportService _reports = new();
    private readonly string _policyPath;

    public DesktopReplayTests()
    {
        Directory.CreateDirectory(_root);
        var executable = Path.Combine(_root, "Target.exe");
        File.WriteAllBytes(executable, [0]);
        _policyPath = Path.Combine(_root, "policy.json");
        File.WriteAllText(_policyPath, $$"""
            {
              "schemaVersion": 1,
              "artifactRoot": {{JsonSerializer.Serialize(_root)}},
              "evidencePolicy": "None",
              "profiles": [
                {
                  "id": "target",
                  "executablePath": {{JsonSerializer.Serialize(executable)}},
                  "arguments": [],
                  "workingDirectory": {{JsonSerializer.Serialize(_root)}},
                  "allowAttach": false,
                  "allowedActions": ["StartTestSession", "CheckUi", "ReplayChecks"],
                  "allowedGlobalHotkeys": {},
                  "allowedShellSurfaces": [],
                  "allowMonitorObservation": false
                }
              ]
            }
            """);
    }

    public void Dispose()
    {
        _key.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task SameVerdictsMatch()
    {
        var reportPath = await WriteOriginalBundleAsync();
        SetupEvaluations(new DesktopUiCheckEvaluation(true, true, 1), new DesktopUiCheckEvaluation(true, false, 1));

        var result = await CreateTools().ReplayChecksAsync(SessionId, reportPath);

        Assert.Equal("matched", result.Status);
        var criterion = Assert.Single(result.Criteria);
        Assert.Equal(("C1", "passed", "passed"), (criterion.Id, criterion.Original, criterion.Replayed));
        Assert.Equal("session-original", result.OriginalSessionRef);
        Assert.Equal(DesktopProofService.KeyIdFor(_key.ExportSubjectPublicKeyInfo()), result.KeyId);
        Assert.All(_reports.Get(SessionId).Checks, check => Assert.StartsWith("replay: ", check.Description, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChangedStateDiffers()
    {
        var reportPath = await WriteOriginalBundleAsync();
        SetupEvaluations(new DesktopUiCheckEvaluation(true, false, 1), new DesktopUiCheckEvaluation(true, false, 1));

        var result = await CreateTools().ReplayChecksAsync(SessionId, reportPath);

        Assert.Equal("differs", result.Status);
        Assert.Equal("failed", result.Criteria[0].Replayed);
        Assert.Equal("failed", result.Checks[0].Replayed);
    }

    [Fact]
    public async Task WindowScopedCheckIsSkipped()
    {
        var reportPath = await WriteOriginalBundleAsync(windowRef: "window-process-1-1A2B");
        SetupEvaluations(new DesktopUiCheckEvaluation(true, false, 1));

        var result = await CreateTools().ReplayChecksAsync(SessionId, reportPath);

        Assert.Equal("WindowRefNotReplayable", result.Checks[0].Skipped);
        Assert.Equal("uncovered", result.Criteria[0].Replayed);
        Assert.Equal("differs", result.Status);
    }

    [Fact]
    public async Task DifferentExecutableIsRejected()
    {
        var reportPath = await WriteOriginalBundleAsync();

        var result = await CreateTools(executableHash: "DEF").ReplayChecksAsync(SessionId, reportPath);

        Assert.Equal("ExecutableMismatch", result.Error?.Code);
        VerifyNothingEvaluated();
    }

    [Fact]
    public async Task EditedReportIsRejected()
    {
        var reportPath = await WriteOriginalBundleAsync();
        var json = await File.ReadAllTextAsync(reportPath);
        await File.WriteAllTextAsync(reportPath, json.Replace("\"verdict\": \"passed\"", "\"verdict\": \"failed\"", StringComparison.Ordinal));

        var result = await CreateTools().ReplayChecksAsync(SessionId, reportPath);

        Assert.Equal("ProofInvalid", result.Error?.Code);
        VerifyNothingEvaluated();
    }

    [Fact]
    public async Task ReportOutsideTheArtifactRootIsRejected()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"pointframe-outside-{Guid.NewGuid():N}.json");

        var result = await CreateTools().ReplayChecksAsync(SessionId, outside);

        Assert.Equal("ReportOutsideArtifactRoot", result.Error?.Code);
    }

    private async Task<string> WriteOriginalBundleAsync(string? windowRef = null)
    {
        var directory = Path.Combine(_root, "session-original");
        var original = new DesktopTestReportService(signer: new Signer(_key));
        original.Initialize("session-original", "Target.exe", ExecutableHash, ["The text box shows hello"], null, directory);
        original.RecordCheck("session-original", new DesktopTestCheckReport(
            "textEquals automationId=textBox expected=hello", DesktopVerificationStatus.Passed, false, "server-uia", null, DateTimeOffset.UtcNow, "C1",
            Spec: new DesktopCheckSpec("textEquals", "textBox", null, null, windowRef, "hello", 5)));
        original.RecordCheck("session-original", new DesktopTestCheckReport(
            "not textEquals automationId=textBox expected=wrong", DesktopVerificationStatus.Passed, false, "server-uia", null, DateTimeOffset.UtcNow, NegativeControl: true,
            Spec: new DesktopCheckSpec("textEquals", "textBox", null, null, null, "wrong", 1)));
        var report = await original.FinalizeAsync("session-original");
        Assert.Equal("passed", report.Verdict);
        await DesktopProofBundle.WriteAsync(report, directory);
        return Path.Combine(directory, DesktopProofBundle.ReportFileName);
    }

    private void SetupEvaluations(params DesktopUiCheckEvaluation[] evaluations)
    {
        var queue = new Queue<DesktopUiCheckEvaluation>(evaluations);
        _checks
            .Setup(service => service.CheckAsync(It.IsAny<DesktopProcessIdentity>(), It.IsAny<DesktopUiCheckCondition>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => queue.Dequeue());
    }

    private void VerifyNothingEvaluated() =>
        _checks.Verify(
            service => service.CheckAsync(It.IsAny<DesktopProcessIdentity>(), It.IsAny<DesktopUiCheckCondition>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never);

    private DesktopTestingMcpTools CreateTools(string executableHash = ExecutableHash)
    {
        var process = new DesktopProcessIdentity("process-1", 1, DateTimeOffset.UtcNow, "Target.exe", executableHash);
        var sessions = new Mock<IDesktopTestSessionService>();
        sessions
            .Setup(service => service.GetAsync(SessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DesktopTestSessionSnapshot(
                SessionId,
                "target",
                DesktopSessionState.Active,
                new DesktopTargetReference("target-1", "target", 1, process, default, LaunchedByDriver: true)));
        return new DesktopTestingMcpTools(
            new Mock<IDesktopActionCoordinator>().Object,
            sessions.Object,
            new Mock<IDesktopObservationService>().Object,
            new Mock<IWindowsDesktopInputService>().Object,
            new Mock<IWindowsUiAutomationActionProvider>().Object,
            _checks.Object,
            new Mock<IDesktopOcrObservationProvider>().Object,
            _reports,
            new Mock<IDesktopEvidenceRecorder>().Object,
            new DesktopTestingPolicyLoader(),
            new DesktopTestingHostOptions(Enabled: true, WorkerMode: false, PolicyPath: _policyPath, WorkerPipeName: null, ParentProcessId: null));
    }

    private sealed class Signer(ECDsa key) : IDesktopProofSigner
    {
        public byte[] PublicKey => key.ExportSubjectPublicKeyInfo();

        public byte[] Sign(byte[] data) => key.SignData(data, HashAlgorithmName.SHA256);
    }
}
