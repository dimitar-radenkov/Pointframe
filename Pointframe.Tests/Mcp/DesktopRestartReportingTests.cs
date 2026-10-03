using System.IO;
using System.Text.Json;
using Moq;
using Pointframe.Engine.Automation.Models;
using Pointframe.Engine.Automation.Services;
using Pointframe.Mcp;
using Pointframe.Mcp.Automation;
using Pointframe.Mcp.Configuration;
using Xunit;

namespace Pointframe.Tests.Mcp;

// A check after a restart only proves "survives a restart" if the signed report shows the restart. Restarts
// used to bypass the coordinator, so the report listed the checks with nothing between them.
public sealed class DesktopRestartReportingTests : IDisposable
{
    private const string SessionId = "session-restart";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pointframe-restart-{Guid.NewGuid():N}");
    private readonly DesktopTestReportService _reports = new();
    private readonly Mock<IDesktopTestSessionService> _sessions = new();
    private readonly string _policyPath;

    public DesktopRestartReportingTests()
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
                  "allowedActions": ["RestartApp"],
                  "allowedGlobalHotkeys": {},
                  "allowedShellSurfaces": [],
                  "allowMonitorObservation": false
                }
              ]
            }
            """);
        var process = new DesktopProcessIdentity("process-1", 1, DateTimeOffset.UtcNow, "Target.exe", "ABC");
        _sessions
            .Setup(service => service.GetAsync(SessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DesktopTestSessionSnapshot(
                SessionId,
                "target",
                DesktopSessionState.Active,
                new DesktopTargetReference("target-1", "target", 1, process, default, LaunchedByDriver: true)));
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task SuccessfulRestartIsRecordedAsAnAction()
    {
        var restarted = new DesktopTestSessionSnapshot(SessionId, "target", DesktopSessionState.Active,
            new DesktopTargetReference("target-2", "target", 2, new DesktopProcessIdentity("process-2", 2, DateTimeOffset.UtcNow, "Target.exe", "ABC"), default, true));
        _sessions
            .Setup(service => service.RestartAsync(SessionId, It.IsAny<DesktopLaunchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DesktopSessionOperationResult(true, "Restarted", "ok", restarted));

        var response = await CreateTools().RestartAppAsync(SessionId, Guid.NewGuid().ToString());

        Assert.Equal("target-2", response.TargetRef);
        var action = Assert.Single(_reports.Get(SessionId).Actions);
        Assert.Equal("restart_app", action.Description);
        Assert.Equal(DesktopDispatchStatus.Complete, action.Result.Dispatch);
    }

    [Fact]
    public async Task RefusedRestartIsRecordedButDoesNotFailTheReport()
    {
        _sessions
            .Setup(service => service.RestartAsync(SessionId, It.IsAny<DesktopLaunchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DesktopSessionOperationResult(false, "TargetStillRunning", "The previous target must exit normally before restart."));

        var response = await CreateTools().RestartAppAsync(SessionId, Guid.NewGuid().ToString());

        Assert.Equal("TargetStillRunning", response.Error?.Code);
        var action = Assert.Single(_reports.Get(SessionId).Actions);
        Assert.Equal("restart_app", action.Description);
        Assert.Equal(DesktopDispatchStatus.NotStarted, action.Result.Dispatch);
    }

    private DesktopTestingMcpTools CreateTools() =>
        new(
            new DesktopActionCoordinator(new DesktopActionLedger(), _reports),
            _sessions.Object,
            new Mock<IDesktopObservationService>().Object,
            new Mock<IWindowsDesktopInputService>().Object,
            new Mock<IWindowsUiAutomationActionProvider>().Object,
            new Mock<IDesktopUiCheckService>().Object,
            new Mock<IDesktopOcrObservationProvider>().Object,
            _reports,
            new Mock<IDesktopEvidenceRecorder>().Object,
            new DesktopTestingPolicyLoader(),
            new DesktopTestingHostOptions(Enabled: true, WorkerMode: false, PolicyPath: _policyPath, WorkerPipeName: null, ParentProcessId: null));
}
