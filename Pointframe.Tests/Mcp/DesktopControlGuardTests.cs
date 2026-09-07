using Pointframe.Mcp.Automation;
using Xunit;

namespace Pointframe.Tests.Mcp;

public sealed class DesktopControlGuardTests
{
    [Fact]
    public void AcquireInitializesNativeResourcesOnTheGuardThread()
    {
        var factory = new FakeAdapterFactory();
        using var guard = new DesktopControlGuard(
            new DesktopControlGuardOptions("user", 7, "default"),
            factory);

        var result = guard.Acquire();

        Assert.True(result.Acquired);
        Assert.NotEqual(Environment.CurrentManagedThreadId, factory.Adapter!.CreatedThreadId);
        Assert.True(guard.Validate().IsValid);
    }

    [Fact]
    public void AcquireReportsMutexOwnershipFailure()
    {
        var factory = new FakeAdapterFactory { AcquireMutexResult = false };
        using var guard = new DesktopControlGuard(adapterFactory: factory);

        var result = guard.Acquire();

        Assert.False(result.Acquired);
        Assert.Equal("DesktopBusy", result.Code);
        Assert.Equal(1, factory.Adapter!.CreateCount);
    }

    [Fact]
    public void AcquireReportsHotKeyRegistrationFailure()
    {
        var factory = new FakeAdapterFactory { RegisterHotKeyResult = false };
        using var guard = new DesktopControlGuard(adapterFactory: factory);

        var result = guard.Acquire();

        Assert.False(result.Acquired);
        Assert.Equal("HotKeyRegistrationFailed", result.Code);
    }

    [Fact]
    public void ValidateDetectsCompetingInputAndPause()
    {
        var factory = new FakeAdapterFactory();
        using var guard = new DesktopControlGuard(adapterFactory: factory);
        Assert.True(guard.Acquire().Acquired);

        factory.Adapter!.CompetingInput = true;
        Assert.Equal("DesktopBusy", guard.Validate().Code);

        factory.Adapter.CompetingInput = false;
        guard.Pause();
        Assert.Equal("SessionPaused", guard.Validate().Code);
    }

    [Fact]
    public void MarkInjectedInputIsPassedToNativeAdapterAndDisposeStopsThread()
    {
        var factory = new FakeAdapterFactory();
        using var guard = new DesktopControlGuard(adapterFactory: factory);
        Assert.True(guard.Acquire().Acquired);

        guard.MarkInjectedInput(TimeSpan.FromSeconds(1));
        guard.Dispose();

        Assert.Equal(TimeSpan.FromSeconds(1), factory.Adapter!.MarkedDuration);
        Assert.True(factory.Adapter.LoopStopped);
    }

    [Fact]
    public void TryReleaseOwnedInputUsesOnlyConfiguredParentFallback()
    {
        var releaseCount = 0;
        using var guard = new DesktopControlGuard(
            parentReleaseFallback: () =>
            {
                releaseCount++;
                return true;
            });

        Assert.True(guard.TryReleaseOwnedInput());
        Assert.Equal(1, releaseCount);
    }

    [Fact]
    public void TryReleaseOwnedInputDoesNotFabricateSuccessWithoutFallback()
    {
        using var guard = new DesktopControlGuard();

        Assert.False(guard.TryReleaseOwnedInput());
    }

    private sealed class FakeAdapterFactory : IDesktopControlNativeAdapterFactory
    {
        public bool AcquireMutexResult { get; init; } = true;

        public bool RegisterHotKeyResult { get; init; } = true;

        public FakeAdapter? Adapter { get; private set; }

        public IDesktopControlNativeAdapter Create()
        {
            Adapter = new FakeAdapter(AcquireMutexResult, RegisterHotKeyResult);
            return Adapter;
        }
    }

    private sealed class FakeAdapter(bool acquireMutexResult, bool registerHotKeyResult) : IDesktopControlNativeAdapter
    {
        private readonly ManualResetEventSlim _stop = new();

        public int CreatedThreadId { get; } = Environment.CurrentManagedThreadId;

        public int CreateCount { get; } = 1;

        public bool CompetingInput { get; set; }

        public bool LoopStopped => _stop.IsSet;

        public TimeSpan? MarkedDuration { get; private set; }

        public bool IsCompetingInputDetected => CompetingInput;

        public bool TryAcquireMutex(string mutexName) => acquireMutexResult;

        public bool TryRegisterPauseHotKey() => registerHotKeyResult;

        public void MarkInjectedInput(TimeSpan duration) => MarkedDuration = duration;

        public void RunMessageLoop(CancellationToken cancellationToken)
        {
            cancellationToken.Register(_stop.Set);
            _stop.Wait();
        }

        public void Dispose() => _stop.Set();
    }
}
