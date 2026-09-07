using Pointframe.Engine.Automation.Models;
using Pointframe.Engine.Automation.Services;
using Xunit;

namespace Pointframe.Tests.Engine;

public sealed class DesktopTestSessionTests
{
    [Fact]
    public async Task StartAsync_CreatesRunningTargetWithGenerationOne()
    {
        var controller = new FakeProcessController();
        var service = new DesktopTestSessionService(controller);

        var result = await service.StartAsync(
            "session-1",
            "pointframe",
            CreateLaunchRequest());

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Session);
        Assert.Equal(DesktopSessionState.Active, result.Session.State);
        Assert.Equal(1, result.Session.Target!.Generation);
        Assert.Equal(DesktopTargetState.Running, result.Session.Target.State);
        Assert.Single(controller.Launched);
    }

    [Fact]
    public async Task RestartAsync_RefusesRunningTargetAndDoesNotLaunchAgain()
    {
        var controller = new FakeProcessController();
        var service = new DesktopTestSessionService(controller);
        await service.StartAsync("session-1", "pointframe", CreateLaunchRequest());

        var result = await service.RestartAsync("session-1", CreateLaunchRequest());

        Assert.False(result.Succeeded);
        Assert.Equal("TargetStillRunning", result.Code);
        Assert.Single(controller.Launched);
    }

    [Fact]
    public async Task RestartAsync_ReleasesExitedTargetAndIncrementsGeneration()
    {
        var controller = new FakeProcessController();
        var service = new DesktopTestSessionService(controller);
        var started = await service.StartAsync("session-1", "pointframe", CreateLaunchRequest());
        var originalProcess = started.Session!.Target!.Process;
        controller.SetState(originalProcess, DesktopTargetState.Exited);

        var result = await service.RestartAsync("session-1", CreateLaunchRequest());

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Session!.Target!.Generation);
        Assert.NotEqual(originalProcess.ProcessRef, result.Session.Target.Process.ProcessRef);
        Assert.Contains(originalProcess.ProcessRef, controller.Released);
        Assert.Equal(2, controller.Launched.Count);
    }

    [Fact]
    public async Task EndAsync_ReportsCleanupIncompleteWithoutTerminatingLiveTarget()
    {
        var controller = new FakeProcessController();
        var service = new DesktopTestSessionService(controller);
        var started = await service.StartAsync("session-1", "pointframe", CreateLaunchRequest());

        var result = await service.EndAsync("session-1");

        Assert.False(result.Succeeded);
        Assert.Equal("CleanupIncomplete", result.Code);
        Assert.Equal(DesktopSessionState.Closed, result.Session!.State);
        Assert.Contains(started.Session!.Target!.Process.ProcessRef, controller.Released);
        Assert.Empty(controller.Terminated);
    }

    private static DesktopLaunchRequest CreateLaunchRequest()
    {
        return new DesktopLaunchRequest(
            @"C:\Pointframe\Pointframe.exe",
            Array.Empty<string>(),
            @"C:\Pointframe");
    }

    private sealed class FakeProcessController : IDesktopProcessController
    {
        private readonly Dictionary<string, DesktopTargetState> _states = new(StringComparer.Ordinal);

        public List<DesktopProcessIdentity> Launched { get; } = [];

        public List<string> Released { get; } = [];

        public List<string> Terminated { get; } = [];

        public Task<DesktopProcessIdentity> LaunchAsync(
            DesktopLaunchRequest request,
            CancellationToken cancellationToken = default)
        {
            var process = new DesktopProcessIdentity(
                $"process-{Launched.Count + 1}",
                Launched.Count + 1,
                DateTimeOffset.UtcNow,
                request.ExecutablePath,
                "HASH");
            Launched.Add(process);
            _states[process.ProcessRef] = DesktopTargetState.Running;
            return Task.FromResult(process);
        }

        public Task<DesktopTargetState> GetStateAsync(
            DesktopProcessIdentity process,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_states.GetValueOrDefault(process.ProcessRef, DesktopTargetState.Unavailable));
        }

        public ValueTask ReleaseAsync(
            DesktopProcessIdentity process,
            CancellationToken cancellationToken = default)
        {
            Released.Add(process.ProcessRef);
            return ValueTask.CompletedTask;
        }

        public void SetState(DesktopProcessIdentity process, DesktopTargetState state)
        {
            _states[process.ProcessRef] = state;
        }
    }
}
