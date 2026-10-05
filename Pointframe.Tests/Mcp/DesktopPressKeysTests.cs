using System.IO;
using System.Text.Json;
using Moq;
using Pointframe.Engine;
using Pointframe.Engine.Automation.Models;
using Pointframe.Engine.Automation.Services;
using Pointframe.Mcp;
using Pointframe.Mcp.Automation;
using Pointframe.Mcp.Configuration;
using Xunit;

namespace Pointframe.Tests.Mcp;

public sealed class DesktopPressKeysTests : IDisposable
{
    private const string SessionId = "session-keys";
    private const string ProcessRef = "process-1";
    private const string WindowRef = "window-process-1-A";
    private const string ObservationRef = "observation-1";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pointframe-keys-{Guid.NewGuid():N}");
    private readonly Mock<IWindowsDesktopInputService> _input = new();
    private readonly Mock<IDesktopObservationService> _observations = new();
    private readonly Mock<IDesktopActionCoordinator> _coordinator = new();
    private readonly string _policyPath;
    private DesktopKeyPressRequest? _request;
    private IReadOnlyList<string>? _invalidated;

    public DesktopPressKeysTests()
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
                  "allowedActions": ["StartTestSession", "PressKeys"],
                  "allowedGlobalHotkeys": { "type-letter": ["SHIFT", "A"] },
                  "allowedShellSurfaces": [],
                  "allowMonitorObservation": false
                }
              ]
            }
            """);

        _input
            .Setup(service => service.PressKeysAsync(It.IsAny<DesktopKeyPressRequest>(), It.IsAny<DesktopProcessIdentity>(), It.IsAny<CancellationToken>()))
            .Callback<DesktopKeyPressRequest, DesktopProcessIdentity, CancellationToken>((request, _, _) => _request = request)
            .ReturnsAsync(DesktopInputPreflightResult.Valid());
        _coordinator
            .Setup(c => c.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<object?>(),
                It.IsAny<Func<CancellationToken, Task<DesktopActionExecution>>>(),
                It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, string, object?, Func<CancellationToken, Task<DesktopActionExecution>>, IReadOnlyList<string>?, CancellationToken>(
                async (_, actionId, _, dispatch, invalidate, token) =>
                {
                    _invalidated = invalidate;
                    var execution = await dispatch(token);
                    return new DesktopActionResult(
                        DesktopTestingLimits.SchemaVersion,
                        actionId,
                        DesktopOperationStatus.Completed,
                        execution.Dispatch,
                        execution.Verification,
                        execution.ObservationStatus,
                        execution.Error);
                });
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task PhysicalPressTargetsTheObservedWindowAndConsumesTheObservation()
    {
        SetupObservation();

        var response = await CreateTools().PressKeysAsync(
            SessionId, Guid.NewGuid().ToString(), [0x10, 0x41], observationRef: ObservationRef, windowRef: WindowRef);

        Assert.Equal("Complete", response.Dispatch);
        Assert.Null(response.Error);
        var window = Assert.IsType<DesktopWindowIdentity>(_request!.Target!.Window);
        Assert.Equal(WindowRef, window.WindowRef);
        Assert.Equal(new nint(0xA), window.NativeHandle);
        Assert.Equal(DesktopInputMethod.Physical, _request.Method);
        Assert.Equal([ObservationRef], _invalidated);
    }

    [Fact]
    public async Task PhysicalPressWithOnlyAnObservationAnchorsToItsBounds()
    {
        SetupObservation();

        var response = await CreateTools().PressKeysAsync(
            SessionId, Guid.NewGuid().ToString(), [0x41], observationRef: ObservationRef);

        Assert.Equal("Complete", response.Dispatch);
        Assert.Null(_request!.Target!.Window);
        Assert.Equal(new PixelBounds(0, 0, 100, 100), _request.Target.BoundsPixels);
        Assert.Equal([ObservationRef], _invalidated);
    }

    [Fact]
    public async Task PhysicalPressWithoutAnyTargetIsRejectedBeforeDispatch()
    {
        var response = await CreateTools().PressKeysAsync(SessionId, Guid.NewGuid().ToString(), [0x41]);

        Assert.Equal("TargetRequired", response.Error?.Code);
        Assert.Null(_request);
    }

    [Fact]
    public async Task PhysicalPressRejectsAStaleObservation()
    {
        _observations
            .Setup(service => service.Resolve(ObservationRef))
            .Throws(new DesktopOperationException("StaleObservation", "stale"));

        var response = await CreateTools().PressKeysAsync(
            SessionId, Guid.NewGuid().ToString(), [0x41], observationRef: ObservationRef, windowRef: WindowRef);

        Assert.Equal("StaleObservation", response.Error?.Code);
        Assert.Null(_request);
    }

    [Fact]
    public async Task PhysicalPressRejectsAWindowThatIsNotPartOfTheObservation()
    {
        SetupObservation();

        var response = await CreateTools().PressKeysAsync(
            SessionId, Guid.NewGuid().ToString(), [0x41], observationRef: ObservationRef, windowRef: "window-process-1-B");

        Assert.Equal("WindowUnavailable", response.Error?.Code);
        Assert.Null(_request);
    }

    [Fact]
    public async Task PhysicalPressRejectsAWindowOfAnotherProcess()
    {
        var response = await CreateTools().PressKeysAsync(
            SessionId, Guid.NewGuid().ToString(), [0x41], windowRef: "window-process-9-A");

        Assert.Equal("WindowUnavailable", response.Error?.Code);
        Assert.Null(_request);
    }

    [Fact]
    public async Task GlobalHotkeyStillSendsNoTargetAndConsumesNothing()
    {
        var response = await CreateTools().PressKeysAsync(
            SessionId, Guid.NewGuid().ToString(), [0x10, 0x41], globalHotkeyId: "type-letter");

        Assert.Equal("Complete", response.Dispatch);
        Assert.Null(_request!.Target);
        Assert.Equal(DesktopInputMethod.GlobalHotkey, _request.Method);
        Assert.Empty(_invalidated!);
    }

    [Fact]
    public async Task UnapprovedGlobalHotkeyIsRejected()
    {
        var response = await CreateTools().PressKeysAsync(
            SessionId, Guid.NewGuid().ToString(), [0x41], globalHotkeyId: "other");

        Assert.Equal("GlobalHotkeyNotApproved", response.Error?.Code);
        Assert.Null(_request);
    }

    [Fact]
    public async Task ServiceSendsKeysToTheForegroundObservedWindow()
    {
        var adapter = new FakeAdapter { Foreground = new nint(0xA) };
        var service = new WindowsDesktopInputService(new FakeFactory(adapter));

        var result = await service.PressKeysAsync(WindowRequest(0xA), Process());

        Assert.True(result.IsValid);
        Assert.Equal(1, adapter.KeyPresses);
    }

    [Fact]
    public async Task ServiceRejectsAWindowThatIsNoLongerForeground()
    {
        var adapter = new FakeAdapter { Foreground = new nint(0xB) };
        var service = new WindowsDesktopInputService(new FakeFactory(adapter));

        var result = await service.PressKeysAsync(WindowRequest(0xA), Process());

        Assert.Equal("FocusRequired", result.Code);
        Assert.Equal(0, adapter.KeyPresses);
    }

    [Fact]
    public async Task ServiceRejectsAWindowThatWasClosed()
    {
        var adapter = new FakeAdapter { Foreground = new nint(0xA), WindowValid = false };
        var service = new WindowsDesktopInputService(new FakeFactory(adapter));

        var result = await service.PressKeysAsync(WindowRequest(0xA), Process());

        Assert.Equal("WindowUnavailable", result.Code);
        Assert.Equal(0, adapter.KeyPresses);
    }

    [Fact]
    public async Task ServiceRejectsAPhysicalPressWithoutATarget()
    {
        var adapter = new FakeAdapter { Foreground = new nint(0xA) };
        var service = new WindowsDesktopInputService(new FakeFactory(adapter));

        var result = await service.PressKeysAsync(new DesktopKeyPressRequest(null, [0x41]), Process());

        Assert.Equal("TargetUnavailable", result.Code);
        Assert.Equal(0, adapter.KeyPresses);
    }

    [Fact]
    public async Task ServiceHotkeyPathNeedsNoTargetOrForeground()
    {
        var adapter = new FakeAdapter { Foreground = new nint(0xB) };
        var service = new WindowsDesktopInputService(new FakeFactory(adapter));

        var result = await service.PressKeysAsync(
            new DesktopKeyPressRequest(null, [0x10, 0x41], DesktopInputMethod.GlobalHotkey, "type-letter"),
            Process());

        Assert.True(result.IsValid);
        Assert.Equal(1, adapter.KeyPresses);
    }

    private static DesktopProcessIdentity Process() =>
        new(ProcessRef, 10, DateTimeOffset.UtcNow, "target.exe", "hash");

    private static DesktopKeyPressRequest WindowRequest(long handle) =>
        new(new DesktopInputTarget(new DesktopWindowIdentity(WindowRef, ProcessRef, new nint(handle))), [0x41]);

    private void SetupObservation()
    {
        var process = Process();
        var observation = new DesktopObservation(
            DesktopTestingLimits.SchemaVersion,
            ObservationRef,
            process,
            DesktopTargetState.Running,
            DesktopObservationStatus.Available,
            [new DesktopImageReference("image-1", 100, 100, new PixelBounds(0, 0, 100, 100), DateTimeOffset.UtcNow)],
            DesktopUiAutomationStatus.Available,
            DateTimeOffset.UtcNow);
        var snapshot = new DesktopUiSnapshot(
            DesktopUiAutomationStatus.Available,
            [new DesktopUiElementSnapshot("element-1", WindowRef, "Edit", "box", "textBox", new PixelBounds(0, 0, 10, 10), true)],
            DateTimeOffset.UtcNow);
        _observations
            .Setup(service => service.Resolve(ObservationRef))
            .Returns(new DesktopObservationResult(observation, snapshot, 1));
    }

    private DesktopTestingMcpTools CreateTools()
    {
        var sessions = new Mock<IDesktopTestSessionService>();
        sessions
            .Setup(service => service.GetAsync(SessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DesktopTestSessionSnapshot(
                SessionId,
                "target",
                DesktopSessionState.Active,
                new DesktopTargetReference("target-1", "target", 1, Process(), default, LaunchedByDriver: true)));
        return new DesktopTestingMcpTools(
            _coordinator.Object,
            sessions.Object,
            _observations.Object,
            _input.Object,
            new Mock<IWindowsUiAutomationActionProvider>().Object,
            new Mock<IDesktopUiCheckService>().Object,
            new Mock<IDesktopOcrObservationProvider>().Object,
            new DesktopTestReportService(),
            new Mock<IDesktopEvidenceRecorder>().Object,
            new DesktopTestingPolicyLoader(),
            new DesktopTestingHostOptions(Enabled: true, WorkerMode: false, PolicyPath: _policyPath, WorkerPipeName: null, ParentProcessId: null));
    }

    private sealed class FakeFactory(FakeAdapter adapter) : IDesktopInputNativeAdapterFactory
    {
        public IDesktopInputNativeAdapter Create() => adapter;
    }

    private sealed class FakeAdapter : IDesktopInputNativeAdapter
    {
        public nint Foreground { get; set; }
        public bool WindowValid { get; set; } = true;
        public int KeyPresses { get; private set; }

        public nint GetForegroundWindow() => Foreground;
        public bool SetForeground(nint handle) => true;
        public bool IsWindowValid(nint handle) => WindowValid;
        public bool IsWindowVisible(nint handle) => true;
        public bool IsPointVisible(PixelBounds bounds, int x, int y) => true;
        public bool SendClick(int x, int y, bool rightButton, int count) => true;
        public bool SendKeys(IReadOnlyList<ushort> virtualKeys)
        {
            KeyPresses++;
            return true;
        }
        public bool SendDrag(IReadOnlyList<PixelBounds> points, int durationMilliseconds) => true;
        public bool SendUnicodeText(string text) => true;
        public bool SendScroll(int? x, int? y, int detents) => true;
        public void ReleaseOwnedInput() { }
    }
}
