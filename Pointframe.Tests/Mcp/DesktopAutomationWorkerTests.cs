using System.IO;
using System.Text.Json;
using Pointframe.Engine;
using Pointframe.Engine.Automation.Models;
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

        public bool ReleaseResult { get; init; } = true;

        public Task<DesktopInputPreflightResult> FocusAsync(DesktopInputTarget target, DesktopProcessIdentity expectedProcess, CancellationToken cancellationToken = default) =>
            Task.FromResult(DesktopInputPreflightResult.Valid());

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

    private sealed class RecordingUiProvider : IWindowsUiAutomationActionProvider
    {
        public bool TryInvoke(string elementRef) => true;

        public bool TrySetValue(string elementRef, string value) => true;
    }
}
