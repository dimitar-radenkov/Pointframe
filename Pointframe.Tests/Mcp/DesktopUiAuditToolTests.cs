using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Server;
using Moq;
using Pointframe.Engine;
using Pointframe.Engine.Automation.Models;
using Pointframe.Engine.Automation.Services;
using Pointframe.Mcp;
using Pointframe.Mcp.Automation;
using Pointframe.Mcp.Configuration;
using Xunit;

namespace Pointframe.Tests.Mcp;

public sealed class DesktopUiAuditToolTests
{
    private const string SessionId = "session-1";
    private static readonly DesktopProcessIdentity Process = new("process-1", 1, DateTimeOffset.UtcNow, "target.exe", "hash");

    private readonly MutableClock _clock = new();
    private readonly DesktopObservationStore _store;
    private readonly Mock<IDisplayCaptureEngine> _capture = new(MockBehavior.Strict);
    private readonly Mock<IDesktopUiObservationProvider> _uia = new(MockBehavior.Strict);
    private readonly Mock<IDesktopTestReportService> _reports = new(MockBehavior.Strict);
    private readonly Mock<IDesktopActionCoordinator> _coordinator = new(MockBehavior.Strict);
    private readonly Mock<IDesktopUiCheckService> _checks = new(MockBehavior.Strict);
    private readonly Mock<IDesktopEvidenceRecorder> _evidence = new(MockBehavior.Strict);
    private readonly Mock<IWindowsDesktopInputService> _input = new(MockBehavior.Strict);
    private readonly Mock<IWindowsUiAutomationActionProvider> _uiActions = new(MockBehavior.Strict);
    private readonly Mock<IDesktopOcrObservationProvider> _ocr = new(MockBehavior.Strict);
    private readonly Mock<IDesktopTestSessionService> _sessions = new();
    private readonly DesktopTestingMcpTools _tools;

    public DesktopUiAuditToolTests()
    {
        _store = new DesktopObservationStore(_clock);
        _sessions
            .Setup(service => service.GetAsync(SessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DesktopTestSessionSnapshot(
                SessionId,
                "profile",
                DesktopSessionState.Active,
                new DesktopTargetReference("target-1", "profile", 1, Process, DesktopTargetState.Running, true)));
        _sessions
            .Setup(service => service.GetAsync(It.Is<string>(id => id != SessionId), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DesktopTestSessionSnapshot?)null);
        var observations = new DesktopObservationService(_capture.Object, _store, _uia.Object, _clock);
        _tools = new DesktopTestingMcpTools(
            _coordinator.Object,
            _sessions.Object,
            observations,
            _input.Object,
            _uiActions.Object,
            _checks.Object,
            _ocr.Object,
            _reports.Object,
            _evidence.Object,
            new DesktopTestingPolicyLoader(),
            new DesktopTestingHostOptions(Enabled: true, WorkerMode: false, PolicyPath: null, WorkerPipeName: null, ParentProcessId: null));
    }

    [Fact]
    public void AuditUi_IsOnlyInTheDesktopTestingToolSet()
    {
        Assert.Contains("desktop_audit_ui", PointframeCommandCatalog.DesktopTestingTools);
        Assert.DoesNotContain("desktop_audit_ui", PointframeCommandCatalog.DirectTools);
        Assert.Contains("desktop_audit_ui", PointframeCommandCatalog.Create(desktopTestingEnabled: true));
        Assert.DoesNotContain("desktop_audit_ui", PointframeCommandCatalog.Create(desktopTestingEnabled: false));
    }

    [Fact]
    public void AuditUi_IsReadOnlyAndNonDestructive()
    {
        var protocol = McpServerTool.Create(AuditMethod(), _ => null!).ProtocolTool;

        Assert.Equal("desktop_audit_ui", protocol.Name);
        Assert.True(protocol.Annotations?.ReadOnlyHint);
        Assert.False(protocol.Annotations?.DestructiveHint);
    }

    [Fact]
    public void AuditUi_DescribesEveryParameter()
    {
        var undescribed = AuditMethod().GetParameters()
            .Where(parameter => parameter.ParameterType != typeof(CancellationToken))
            .Where(parameter => string.IsNullOrWhiteSpace(parameter.GetCustomAttribute<DescriptionAttribute>()?.Description))
            .Select(parameter => parameter.Name)
            .ToArray();

        Assert.Empty(undescribed);
        Assert.Equal(
            new[] { "sessionId", "observationRef" },
            AuditMethod().GetParameters().Where(parameter => parameter.ParameterType != typeof(CancellationToken)).Select(parameter => parameter.Name!).ToArray());
    }

    [Fact]
    public async Task AuditUi_UnknownSession_IsRejectedWithSessionNotFound()
    {
        var observation = Save("observation-a", Element("e1", "TODO"));

        var response = await _tools.AuditUiAsync("no-such-session", observation);

        Assert.Equal("SessionNotFound", response.Error?.Code);
        Assert.Empty(response.Findings);
        Assert.Null(response.Coverage);
        Assert.True(response.Advisory);
    }

    [Fact]
    public async Task AuditUi_UnknownObservation_IsRejectedAsStaleWithoutReobserving()
    {
        var response = await _tools.AuditUiAsync(SessionId, "observation-missing");

        Assert.Equal("StaleObservation", response.Error?.Code);
        Assert.Equal("desktop_observe_app", response.NextStep?.Tool);
        Assert.Empty(response.Findings);
        AssertNothingCapturedInspectedOrReported();
    }

    [Fact]
    public async Task AuditUi_ExpiredObservation_IsRejectedAsStaleWithoutReobserving()
    {
        var observation = Save("observation-a", Element("e1", "TODO"));
        _clock.Now = _clock.Now.AddSeconds(DesktopTestingLimits.ObservationTtlSeconds + 1);

        var response = await _tools.AuditUiAsync(SessionId, observation);

        Assert.Equal("StaleObservation", response.Error?.Code);
        Assert.Empty(response.Findings);
        AssertNothingCapturedInspectedOrReported();
    }

    [Fact]
    public async Task AuditUi_ObservationOfAnotherSession_IsRejectedWithoutAuditing()
    {
        var other = new DesktopProcessIdentity("process-other", 2, DateTimeOffset.UtcNow, "other.exe", "hash2");
        var observation = Save("observation-other", other, Element("e1", "TODO"));

        var response = await _tools.AuditUiAsync(SessionId, observation);

        Assert.Equal("ObservationNotInSession", response.Error?.Code);
        Assert.Empty(response.Findings);
        Assert.Null(response.Coverage);
        AssertNothingCapturedInspectedOrReported();
    }

    [Fact]
    public async Task AuditUi_HappyPath_ReturnsNumberedFindingsCoverageAndAdvisoryFlag()
    {
        var observation = Save(
            "observation-a",
            Element("e1", "TODO"),
            Element("e2", "Welcome, {0}"),
            Element("e3", "Fine"),
            Element("e4", "FIXME", role: "Edit"));

        var response = await _tools.AuditUiAsync(SessionId, observation);

        Assert.Null(response.Error);
        Assert.Equal(observation, response.ObservationRef);
        Assert.True(response.Advisory);
        Assert.Contains("do not affect", response.AdvisoryNote);
        Assert.Equal(new[] { "F1", "F2" }, response.Findings.Select(finding => finding.Id).ToArray());
        var first = response.Findings[0];
        Assert.Equal("placeholder-text", first.RuleId);
        Assert.Equal(1, first.RuleVersion);
        Assert.Equal("medium", first.Severity);
        Assert.Equal("high", first.Confidence);
        Assert.Equal("TODO", first.MatchedToken);
        Assert.Equal("e1", first.ElementRef);
        Assert.Equal("window-1", first.WindowRef);
        Assert.Equal("Text", first.Role);
        Assert.Equal("auto-e1", first.AutomationId);
        Assert.Equal(new McpPixelBounds(10, 20, 30, 40), first.BoundsPixels);
        Assert.Equal("medium", response.Findings[1].Confidence);
        var coverage = response.Coverage!;
        Assert.Equal(new[] { new DesktopUiAuditRuleResponse("placeholder-text", 1) }, coverage.RulesRun.ToArray());
        Assert.Equal(3, coverage.ElementsInspected);
        Assert.Equal(1, coverage.ElementsSkipped["editable-role"]);
        Assert.Equal("not-requested", coverage.OcrAvailable);
        Assert.False(coverage.UiaTruncated);
    }

    [Fact]
    public async Task AuditUi_SerializedResponse_CarriesAdvisoryTrueAndCoverageFields()
    {
        var observation = Save("observation-a", Element("e1", "TODO"));

        var response = await _tools.AuditUiAsync(SessionId, observation);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var root = document.RootElement;
        Assert.True(root.GetProperty("advisory").GetBoolean());
        Assert.Equal("F1", root.GetProperty("findings")[0].GetProperty("id").GetString());
        var coverage = root.GetProperty("coverage");
        Assert.Equal(1, coverage.GetProperty("elementsInspected").GetInt32());
        Assert.Equal("not-requested", coverage.GetProperty("ocrAvailable").GetString());
        Assert.False(coverage.GetProperty("uiaTruncated").GetBoolean());
        Assert.True(coverage.TryGetProperty("rulesRun", out _));
        Assert.True(coverage.TryGetProperty("elementsSkipped", out _));
    }

    [Fact]
    public async Task AuditUi_SerializedResponse_IncludesOcrSuppressionCoverage()
    {
        var observation = Save(
            "observation-a",
            Process,
            new DesktopObservationOcrResult("image-9", "TODO buy milk"),
            Element("e1", "", role: "Edit") with { Text = "TODO buy milk" });

        var response = await _tools.AuditUiAsync(SessionId, observation);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(1, document.RootElement.GetProperty("coverage").GetProperty("ocrLinesSuppressedAsUserContent").GetInt32());
        Assert.Empty(response.Findings);
    }

    [Fact]
    public async Task AuditUi_OcrOnlyMatch_ReportsImageRefAndOcrLine()
    {
        var observation = Save(
            "observation-a",
            Process,
            new DesktopObservationOcrResult("image-9", "Header\nlorem ipsum dolor"),
            Element("e1", "Header"));

        var response = await _tools.AuditUiAsync(SessionId, observation);

        var finding = Assert.Single(response.Findings);
        Assert.Equal("low", finding.Severity);
        Assert.Equal("low", finding.Confidence);
        Assert.Equal("image-9", finding.ImageRef);
        Assert.Equal("lorem ipsum dolor", finding.OcrLine);
        Assert.Null(finding.ElementRef);
        Assert.Equal("true", response.Coverage!.OcrAvailable);
    }

    [Fact]
    public async Task AuditUi_NeverCapturesInspectsOrWritesToTheReport()
    {
        var observation = Save("observation-a", Element("e1", "TODO"), Element("e2", "{0}"));

        var first = await _tools.AuditUiAsync(SessionId, observation);
        var second = await _tools.AuditUiAsync(SessionId, observation);

        Assert.Equal(2, first.Findings.Count);
        Assert.Equal(first.Findings, second.Findings);
        AssertNothingCapturedInspectedOrReported();
        // The stored observation stays usable: auditing does not consume it.
        Assert.Same(_store.Resolve(observation), _store.Resolve(observation));
    }

    private void AssertNothingCapturedInspectedOrReported()
    {
        _capture.VerifyNoOtherCalls();
        _uia.VerifyNoOtherCalls();
        _reports.VerifyNoOtherCalls();
        _coordinator.VerifyNoOtherCalls();
        _checks.VerifyNoOtherCalls();
        _evidence.VerifyNoOtherCalls();
        _input.VerifyNoOtherCalls();
        _uiActions.VerifyNoOtherCalls();
        _ocr.VerifyNoOtherCalls();
    }

    private string Save(string observationRef, params DesktopUiElementSnapshot[] elements) =>
        Save(observationRef, Process, null, elements);

    private string Save(string observationRef, DesktopProcessIdentity process, params DesktopUiElementSnapshot[] elements) =>
        Save(observationRef, process, null, elements);

    private string Save(
        string observationRef,
        DesktopProcessIdentity process,
        DesktopObservationOcrResult? ocr,
        params DesktopUiElementSnapshot[] elements)
    {
        var now = _clock.Now;
        _store.Save(new DesktopObservationResult(
            new DesktopObservation(
                1, observationRef, process, DesktopTargetState.Running, DesktopObservationStatus.Available, [],
                DesktopUiAutomationStatus.Available, now),
            new DesktopUiSnapshot(DesktopUiAutomationStatus.Available, elements, now),
            1,
            ocr));
        return observationRef;
    }

    private static DesktopUiElementSnapshot Element(string elementRef, string name, string role = "Text") =>
        new(elementRef, "window-1", role, name, "auto-" + elementRef, new PixelBounds(10, 20, 30, 40), true);

    private static MethodInfo AuditMethod() =>
        typeof(DesktopTestingMcpTools).GetMethod(nameof(DesktopTestingMcpTools.AuditUiAsync))!;

    private sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
