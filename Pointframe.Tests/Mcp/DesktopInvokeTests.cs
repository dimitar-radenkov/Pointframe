using Moq;
using Pointframe.Engine;
using Pointframe.Engine.Automation.Models;
using Pointframe.Engine.Automation.Services;
using Pointframe.Mcp;
using Pointframe.Mcp.Automation;
using Pointframe.Mcp.Configuration;
using Xunit;

namespace Pointframe.Tests.Mcp;

public sealed class DesktopInvokeTests
{
    private const string SessionId = "session-invoke";
    private const string ProcessRef = "process-1";
    private const string WindowRef = "window-process-1-A";
    private const string ElementRef = "el-1-2";
    private const string ObservationRef = "observation-1";

    private static readonly PixelBounds Bounds = new(100, 200, 40, 20);

    [Fact]
    public async Task ElementWithBoundsUsesCenterClickWithoutCallingPattern()
    {
        var fixture = new InvokeFixture(new PixelBounds(100, 200, 40, 20));

        var response = await fixture.Tools.InvokeAsync(SessionId, Guid.NewGuid().ToString(), ElementRef);

        Assert.Equal("click", response.Method);
        Assert.Equal("Complete", response.Dispatch);
        fixture.Input.Verify(input => input.FocusAsync(
            It.Is<DesktopInputTarget>(target => target.Window!.WindowRef == WindowRef),
            It.IsAny<DesktopProcessIdentity>(),
            It.IsAny<CancellationToken>()), Times.Once);
        fixture.Input.Verify(input => input.ClickAsync(
            It.Is<DesktopClickRequest>(click => click.X == 120 && click.Y == 210
                && click.Target.Window!.WindowRef == WindowRef
                && click.Target.BoundsPixels == Bounds),
            It.IsAny<DesktopProcessIdentity>(),
            It.IsAny<CancellationToken>()), Times.Once);
        fixture.UiAutomation.Verify(provider => provider.TryInvoke(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ZeroSizeElementUsesPattern()
    {
        var fixture = new InvokeFixture(new PixelBounds(100, 200, 0, 20));
        fixture.UiAutomation.Setup(provider => provider.TryInvoke(ElementRef)).Returns(UiInvokeOutcome.Completed);

        var response = await fixture.Tools.InvokeAsync(SessionId, Guid.NewGuid().ToString(), ElementRef);

        Assert.Equal("pattern", response.Method);
        Assert.Equal("Complete", response.Dispatch);
        fixture.Input.Verify(input => input.FocusAsync(
            It.IsAny<DesktopInputTarget>(),
            It.IsAny<DesktopProcessIdentity>(),
            It.IsAny<CancellationToken>()), Times.Never);
        fixture.Input.Verify(input => input.ClickAsync(
            It.IsAny<DesktopClickRequest>(),
            It.IsAny<DesktopProcessIdentity>(),
            It.IsAny<CancellationToken>()), Times.Never);
        fixture.UiAutomation.Verify(provider => provider.TryInvoke(ElementRef), Times.Once);
    }

    [Fact]
    public async Task RejectedClickPreflightFallsBackToPattern()
    {
        var fixture = new InvokeFixture(Bounds);
        fixture.Input
            .Setup(input => input.ClickAsync(It.IsAny<DesktopClickRequest>(), It.IsAny<DesktopProcessIdentity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DesktopInputPreflightResult.Invalid("OccludedOrOutOfBounds", "The click point is not visible."));
        fixture.UiAutomation.Setup(provider => provider.TryInvoke(ElementRef)).Returns(UiInvokeOutcome.Completed);

        var response = await fixture.Tools.InvokeAsync(SessionId, Guid.NewGuid().ToString(), ElementRef);

        Assert.Equal("pattern", response.Method);
        Assert.Equal("Complete", response.Dispatch);
        fixture.UiAutomation.Verify(provider => provider.TryInvoke(ElementRef), Times.Once);
    }

    [Fact]
    public async Task UncertainNativeClickIsNotReplayedAsPattern()
    {
        var fixture = new InvokeFixture(Bounds);
        fixture.Input
            .Setup(input => input.ClickAsync(It.IsAny<DesktopClickRequest>(), It.IsAny<DesktopProcessIdentity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DesktopInputPreflightResult.Invalid("InputDispatchFailed", "The native click may have partially reached the target."));

        var response = await fixture.Tools.InvokeAsync(SessionId, Guid.NewGuid().ToString(), ElementRef);

        Assert.Equal("click", response.Method);
        Assert.Equal("Unknown", response.Dispatch);
        fixture.UiAutomation.Verify(provider => provider.TryInvoke(It.IsAny<string>()), Times.Never);
    }

    private sealed class InvokeFixture
    {
        public Mock<IWindowsDesktopInputService> Input { get; } = new();
        public Mock<IWindowsUiAutomationActionProvider> UiAutomation { get; } = new();
        public DesktopTestingMcpTools Tools { get; } = null!;

        public InvokeFixture(PixelBounds bounds)
        {
            Tools = CreateTools(bounds, Input, UiAutomation);
        }

        private static DesktopTestingMcpTools CreateTools(
            PixelBounds elementBounds,
            Mock<IWindowsDesktopInputService> input,
            Mock<IWindowsUiAutomationActionProvider> uiAutomation)
        {
            var process = new DesktopProcessIdentity(ProcessRef, 1, DateTimeOffset.UtcNow, "target.exe", "hash");
            var sessions = new Mock<IDesktopTestSessionService>();
            sessions.Setup(service => service.GetAsync(SessionId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DesktopTestSessionSnapshot(
                    SessionId,
                    "target",
                    DesktopSessionState.Active,
                    new DesktopTargetReference("target-1", "target", 1, process, default, LaunchedByDriver: true)));

            var observations = new Mock<IDesktopObservationService>();
            observations.Setup(service => service.ResolveElement(ElementRef, ProcessRef))
                .Returns(new DesktopObservedElement(
                    ObservationRef,
                    new DesktopUiElementSnapshot(ElementRef, WindowRef, "Button", "Open", "open", elementBounds, true)));

            var coordinator = new Mock<IDesktopActionCoordinator>();
            coordinator.Setup(service => service.ExecuteAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<object?>(),
                    It.IsAny<Func<CancellationToken, Task<DesktopActionExecution>>>(),
                    It.IsAny<IReadOnlyList<string>?>(),
                    It.IsAny<CancellationToken>()))
                .Returns<string, string, object?, Func<CancellationToken, Task<DesktopActionExecution>>, IReadOnlyList<string>?, CancellationToken>(
                    async (sessionId, actionId, _, dispatch, _, token) =>
                    {
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

            var reports = new Mock<IDesktopTestReportService>();
            reports.Setup(service => service.IsActionUnannotated(It.IsAny<string>(), It.IsAny<string>())).Returns(false);
            input.Setup(service => service.FocusAsync(It.IsAny<DesktopInputTarget>(), It.IsAny<DesktopProcessIdentity>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(DesktopInputPreflightResult.Valid());
            input.Setup(service => service.ClickAsync(It.IsAny<DesktopClickRequest>(), It.IsAny<DesktopProcessIdentity>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(DesktopInputPreflightResult.Valid());
            input.Setup(service => service.TryReleaseOwnedInput()).Returns(true);

            return new DesktopTestingMcpTools(
                coordinator.Object,
                sessions.Object,
                observations.Object,
                input.Object,
                uiAutomation.Object,
                new Mock<IDesktopUiCheckService>().Object,
                new Mock<IDesktopOcrObservationProvider>().Object,
                reports.Object,
                new Mock<IDesktopEvidenceRecorder>().Object,
                new DesktopTestingPolicyLoader(),
                new DesktopTestingHostOptions(Enabled: true, WorkerMode: false, PolicyPath: null, WorkerPipeName: null, ParentProcessId: null));
        }
    }
}
