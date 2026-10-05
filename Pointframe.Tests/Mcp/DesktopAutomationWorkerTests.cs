using System.IO;
using System.Text.Json;
using Pointframe.Engine;
using Pointframe.Engine.Automation.Models;
using Pointframe.Engine.Automation.Services;
using Pointframe.Mcp.Automation;
using Pointframe.Mcp.Configuration;
using Xunit;

namespace Pointframe.Tests.Mcp;

public sealed class DesktopAutomationWorkerTests
{
    [Fact]
    public void OptionsParseWorkerAndEnabledModes()
    {
        var options = DesktopTestingHostOptions.Parse(
        [
            "--desktop-testing",
            "--desktop-policy",
            "policy.json",
            "--desktop-worker",
            "--desktop-pipe",
            "pipe-1",
            "--desktop-parent-pid",
            "42",
        ]);

        Assert.True(options.Enabled);
        Assert.True(options.WorkerMode);
        Assert.Equal("policy.json", options.PolicyPath);
        Assert.Equal("pipe-1", options.WorkerPipeName);
        Assert.Equal(42, options.ParentProcessId);
    }

    [Fact]
    public void ProtocolRejectsOversizedMessages()
    {
        var payload = new string('x', DesktopAutomationWorkerProtocol.MaxMessageBytes);

        Assert.Throws<InvalidDataException>(() =>
            DesktopAutomationWorkerProtocol.Serialize(new DesktopAutomationWorkerRequest(1, "id", "test", payload)));
    }

    [Fact]
    public async Task StalledProviderCanBeCancelledWithoutReplay()
    {
        var provider = new BlockingProvider();
        var request = new DesktopAutomationWorkerRequest(1, "request-1", "observe");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.HandleAsync(request, cancellation.Token));

        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public void WorkerProtocolRequiresVersionedRequests()
    {
        var request = new DesktopAutomationWorkerRequest(1, "id", "observe");
        var json = DesktopAutomationWorkerProtocol.Serialize(request);
        var roundTrip = DesktopAutomationWorkerProtocol.Deserialize<DesktopAutomationWorkerRequest>(json);

        Assert.Equal(1, roundTrip.ProtocolVersion);
        Assert.Equal("id", roundTrip.RequestId);
    }

    [Fact]
    public async Task ProviderDispatchesInputThroughTheWorkerBoundary()
    {
        var input = new RecordingInputService();
        var provider = new DesktopAutomationWorkerProvider(input, new RecordingUiProvider());
        var process = new DesktopProcessIdentity("process-1", 1, DateTimeOffset.UtcNow, "target.exe", "hash");
        var request = new DesktopClickRequest(
            new DesktopInputTarget(BoundsPixels: new PixelBounds(0, 0, 100, 100)),
            10,
            20);
        var workerRequest = new DesktopAutomationWorkerRequest(
            1,
            "request-1",
            DesktopAutomationWorkerProtocol.Operations.Click,
            JsonSerializer.Serialize(new DesktopAutomationWorkerInputRequest(DesktopAutomationWorkerProtocol.Operations.Click, request, process)));

        var response = await provider.HandleAsync(workerRequest, CancellationToken.None);

        Assert.True(response.Succeeded);
        Assert.Equal(1, input.Clicks);
    }

    [Fact]
    public async Task ProviderPreservesNativeWindowHandleAcrossTheWorkerBoundary()
    {
        var input = new RecordingInputService();
        var provider = new DesktopAutomationWorkerProvider(input, new RecordingUiProvider());
        var process = new DesktopProcessIdentity("process-1", 1, DateTimeOffset.UtcNow, "target.exe", "hash");
        var target = new DesktopInputTarget(
            new DesktopWindowIdentity("window-process-1-1234", process.ProcessRef, new nint(0x1234)));
        var workerRequest = new DesktopAutomationWorkerRequest(
            1,
            "request-focus",
            DesktopAutomationWorkerProtocol.Operations.Focus,
            DesktopAutomationWorkerProtocol.SerializePayload(
                new DesktopAutomationWorkerInputRequest(DesktopAutomationWorkerProtocol.Operations.Focus, target, process)));

        var response = await provider.HandleAsync(workerRequest, CancellationToken.None);

        Assert.True(response.Succeeded);
        Assert.Equal(new nint(0x1234), input.FocusedHandle);
    }

    [Fact]
    public async Task ProviderPreflightFailureDoesNotReachNativeInput()
    {
        var input = new RecordingInputService();
        var provider = new DesktopAutomationWorkerProvider(input, new RecordingUiProvider());
        var process = new DesktopProcessIdentity("process-1", 1, DateTimeOffset.UtcNow, "target.exe", "hash");
        var request = new DesktopClickRequest(
            new DesktopInputTarget(BoundsPixels: new PixelBounds(0, 0, 10, 10)),
            20,
            20);
        var workerRequest = new DesktopAutomationWorkerRequest(
            1,
            "request-2",
            DesktopAutomationWorkerProtocol.Operations.Click,
            JsonSerializer.Serialize(new DesktopAutomationWorkerInputRequest(DesktopAutomationWorkerProtocol.Operations.Click, request, process)));

        var response = await provider.HandleAsync(workerRequest, CancellationToken.None);

        Assert.False(response.Succeeded);
        Assert.Equal("OccludedOrOutOfBounds", response.Code);
        Assert.Equal(0, input.Clicks);
    }

    [Fact]
    public async Task ProviderReportsInputReleaseFailure()
    {
        var input = new RecordingInputService { ReleaseResult = false };
        var provider = new DesktopAutomationWorkerProvider(input, new RecordingUiProvider());
        var request = new DesktopAutomationWorkerRequest(1, "release-1", DesktopAutomationWorkerProtocol.Operations.Release, "{}");

        var response = await provider.HandleAsync(request, CancellationToken.None);

        Assert.False(response.Succeeded);
        Assert.Equal("InputReleaseFailed", response.Code);
    }

    private sealed class BlockingProvider : IDesktopAutomationWorkerProvider
    {
        public int CallCount { get; private set; }

        public async Task<DesktopAutomationWorkerResponse> HandleAsync(
            DesktopAutomationWorkerRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new DesktopAutomationWorkerResponse(1, request.RequestId, true, "Ok");
        }
    }

    private sealed class RecordingInputService : IWindowsDesktopInputService
    {
        public int Clicks { get; private set; }

        public nint FocusedHandle { get; private set; }

        public bool ReleaseResult { get; init; } = true;

        public Task<DesktopInputPreflightResult> FocusAsync(DesktopInputTarget target, DesktopProcessIdentity expectedProcess, CancellationToken cancellationToken = default)
        {
            FocusedHandle = target.Window?.NativeHandle ?? nint.Zero;
            return Task.FromResult(DesktopInputPreflightResult.Valid());
        }

        public Task<DesktopInputPreflightResult> ClickAsync(DesktopClickRequest request, DesktopProcessIdentity expectedProcess, CancellationToken cancellationToken = default)
        {
            if (request.Target.BoundsPixels is not { } bounds
                || request.X < bounds.X
                || request.Y < bounds.Y
                || request.X >= bounds.X + bounds.Width
                || request.Y >= bounds.Y + bounds.Height)
            {
                return Task.FromResult(DesktopInputPreflightResult.Invalid(
                    "OccludedOrOutOfBounds",
                    "The click point is not visible within the approved target."));
            }

            Clicks++;
            return Task.FromResult(DesktopInputPreflightResult.Valid());
        }

        public Task<DesktopInputPreflightResult> PressKeysAsync(DesktopKeyPressRequest request, DesktopProcessIdentity expectedProcess, CancellationToken cancellationToken = default) =>
            Task.FromResult(DesktopInputPreflightResult.Valid());

        public Task<DesktopInputPreflightResult> DragAsync(DesktopDragRequest request, DesktopProcessIdentity expectedProcess, CancellationToken cancellationToken = default) =>
            Task.FromResult(DesktopInputPreflightResult.Valid());

        public Task<DesktopInputPreflightResult> EnterTextAsync(DesktopTextRequest request, DesktopProcessIdentity expectedProcess, CancellationToken cancellationToken = default) =>
            Task.FromResult(DesktopInputPreflightResult.Valid());

        public Task<DesktopInputPreflightResult> ScrollAsync(DesktopScrollRequest request, DesktopProcessIdentity expectedProcess, CancellationToken cancellationToken = default) =>
            Task.FromResult(DesktopInputPreflightResult.Valid());

        public void ReleaseOwnedInput()
        {
        }

        public bool TryReleaseOwnedInput() => ReleaseResult;
    }

    [Fact]
    public async Task InspectOfAFullSizedWindowCrossesTheWorkerBoundary()
    {
        // 193 elements of an ordinary WPF window (dnGrep) exceeded the old 64 KiB limit; the worker then
        // closed the pipe and every observation came back ProviderUnavailable.
        var elements = Enumerable.Range(0, DesktopTestingLimits.MaxUiAutomationElements)
            .Select(index => new DesktopUiElementSnapshot(
                $"el-1-{index}", "window-process-1-1234", "Button", $"Button number {index} with a long name", $"button{index}",
                new PixelBounds(index, index, 120, 24), true, Text: new string('t', 200)))
            .ToArray();
        var provider = new DesktopAutomationWorkerProvider(new RecordingInputService(), new SnapshotUiProvider(elements));

        var response = await provider.HandleAsync(InspectRequest(), CancellationToken.None);
        var roundTrip = DesktopAutomationWorkerProtocol.Deserialize<DesktopAutomationWorkerResponse>(DesktopAutomationWorkerProtocol.Serialize(response));

        Assert.True(roundTrip.Succeeded);
        var snapshot = JsonSerializer.Deserialize<DesktopUiSnapshot>(roundTrip.Payload!);
        Assert.Equal(DesktopTestingLimits.MaxUiAutomationElements, snapshot!.Elements.Count);
    }

    [Fact]
    public async Task InspectTooLargeForTheProtocolFailsWithoutBreakingTheWorker()
    {
        var huge = new string('x', DesktopAutomationWorkerProtocol.MaxMessageBytes / 4);
        var elements = Enumerable.Range(0, 4)
            .Select(index => new DesktopUiElementSnapshot($"el-1-{index}", "window-process-1-1234", "Document", "doc", null, new PixelBounds(0, 0, 10, 10), true, Text: huge))
            .ToArray();
        var provider = new DesktopAutomationWorkerProvider(new RecordingInputService(), new SnapshotUiProvider(elements));

        var response = await provider.HandleAsync(InspectRequest(), CancellationToken.None);

        Assert.False(response.Succeeded);
        Assert.Equal("SnapshotTooLarge", response.Code);
        DesktopAutomationWorkerProtocol.Serialize(response);
    }

    private static DesktopAutomationWorkerRequest InspectRequest()
    {
        var process = new DesktopProcessIdentity("process-1", 1, DateTimeOffset.UtcNow, "target.exe", "hash");
        return new DesktopAutomationWorkerRequest(
            1,
            "request-inspect",
            DesktopAutomationWorkerProtocol.Operations.Inspect,
            JsonSerializer.Serialize(new DesktopObservationRequest(process, [new PixelBounds(0, 0, 100, 100)])));
    }

    private sealed class SnapshotUiProvider(IReadOnlyList<DesktopUiElementSnapshot> elements) : IWindowsUiAutomationActionProvider, IDesktopUiObservationProvider
    {
        public bool TryInvoke(string elementRef) => true;

        public bool TrySetValue(string elementRef, string value) => true;

        public DesktopUiSnapshot Inspect(DesktopObservationRequest request) =>
            new(DesktopUiAutomationStatus.Available, elements, DateTimeOffset.UtcNow);
    }

    private sealed class RecordingUiProvider : IWindowsUiAutomationActionProvider
    {
        public bool TryInvoke(string elementRef) => true;

        public bool TrySetValue(string elementRef, string value) => true;
    }
}
