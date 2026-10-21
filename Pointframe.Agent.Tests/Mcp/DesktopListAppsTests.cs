using System.IO;
using System.Text.Json;
using Moq;
using Pointframe.Engine.Automation.Services;
using Pointframe.Mcp;
using Pointframe.Mcp.Automation;
using Pointframe.Mcp.Configuration;
using Xunit;

namespace Pointframe.Tests.Mcp;

public sealed class DesktopListAppsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Pointframe.ListAppsTests", Guid.NewGuid().ToString("N"));

    public DesktopListAppsTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task ListApps_ReturnsTheLoadedPolicysProfilesAndKeepsTheSuccessShape()
    {
        var executable = Path.Combine(_root, "Target.exe");
        File.WriteAllBytes(executable, [0]);
        var policyPath = Path.Combine(_root, "policy.json");
        File.WriteAllText(policyPath, $$"""
            {
              "schemaVersion": 1,
              "artifactRoot": {{JsonSerializer.Serialize(_root)}},
              "evidencePolicy": "All",
              "profiles": [
                {
                  "id": "target",
                  "executablePath": {{JsonSerializer.Serialize(executable)}},
                  "arguments": [],
                  "workingDirectory": {{JsonSerializer.Serialize(_root)}},
                  "allowAttach": false,
                  "allowedActions": ["StartTestSession", "ObserveApp", "Invoke"],
                  "allowedGlobalHotkeys": {},
                  "allowedShellSurfaces": [],
                  "allowMonitorObservation": false
                }
              ]
            }
            """);
        var tools = CreateTools(policyPath);
        var actionId = Guid.NewGuid().ToString();

        var response = await tools.ListAppsAsync(actionId);

        Assert.Equal("Completed", response.OperationStatus);
        Assert.Equal("Complete", response.Dispatch);
        Assert.Null(response.Error);
        var app = Assert.Single(response.Apps!);
        Assert.Equal("target", app.Id);
        Assert.Equal("Target.exe", app.ExecutableName);
        Assert.Equal(3, app.AllowedActionCount);
    }

    [Fact]
    public async Task ListApps_WithoutAUsablePolicyReportsPolicyUnavailable()
    {
        var response = await CreateTools(null).ListAppsAsync(Guid.NewGuid().ToString());

        Assert.Equal("PolicyUnavailable", response.Error?.Code);
        Assert.Null(response.Apps);
    }

    private static DesktopTestingMcpTools CreateTools(string? policyPath) => new(
        new Mock<IDesktopActionCoordinator>().Object,
        new Mock<IDesktopTestSessionService>().Object,
        new Mock<IDesktopObservationService>().Object,
        new Mock<IWindowsDesktopInputService>().Object,
        new Mock<IWindowsUiAutomationActionProvider>().Object,
        new Mock<IDesktopUiCheckService>().Object,
        new Mock<IDesktopOcrObservationProvider>().Object,
        new Mock<IDesktopTestReportService>().Object,
        new Mock<IDesktopEvidenceRecorder>().Object,
        new DesktopTestingPolicyLoader(),
        new DesktopTestingHostOptions(Enabled: true, WorkerMode: false, PolicyPath: policyPath, WorkerPipeName: null, ParentProcessId: null));
}
