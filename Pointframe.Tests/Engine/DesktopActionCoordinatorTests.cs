using Pointframe.Engine.Automation.Models;
using Pointframe.Engine.Automation.Services;
using Xunit;

namespace Pointframe.Tests.Engine;

public sealed class DesktopActionCoordinatorTests
{
    [Fact]
    public async Task RepeatedActionIdDoesNotDispatchTwice()
    {
        var coordinator = new DesktopActionCoordinator(new DesktopActionLedger(), new DesktopTestReportService());
        var dispatchCount = 0;
        var actionId = Guid.NewGuid().ToString();

        var first = await coordinator.ExecuteAsync("session-1", actionId, new { value = 1 }, _ =>
        {
            dispatchCount++;
            return Task.FromResult(new DesktopActionExecution(DesktopDispatchStatus.Complete));
        });
        var second = await coordinator.ExecuteAsync("session-1", actionId, new { value = 1 }, _ =>
        {
            dispatchCount++;
            return Task.FromResult(new DesktopActionExecution(DesktopDispatchStatus.Complete));
        });

        Assert.Equal(1, dispatchCount);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task ConflictingActionIdIsRejected()
    {
        var coordinator = new DesktopActionCoordinator(new DesktopActionLedger(), new DesktopTestReportService());
        var actionId = Guid.NewGuid().ToString();
        await coordinator.ExecuteAsync("session-1", actionId, new { value = 1 }, _ =>
            Task.FromResult(new DesktopActionExecution(DesktopDispatchStatus.Complete)));

        var result = await coordinator.ExecuteAsync("session-1", actionId, new { value = 2 }, _ =>
            Task.FromResult(new DesktopActionExecution(DesktopDispatchStatus.Complete)));

        Assert.Equal("ActionIdConflict", result.Error?.Code);
        Assert.Equal(DesktopDispatchStatus.NotStarted, result.Dispatch);
    }

    [Fact]
    public async Task CancellationProducesUnknownDispatchAndDoesNotReplay()
    {
        var coordinator = new DesktopActionCoordinator(new DesktopActionLedger(), new DesktopTestReportService());
        var actionId = Guid.NewGuid().ToString();
        using var cancellation = new CancellationTokenSource();
        var result = await coordinator.ExecuteAsync("session-1", actionId, null, async _ =>
        {
            cancellation.Cancel();
            await Task.Delay(1, cancellation.Token);
            return new DesktopActionExecution(DesktopDispatchStatus.Complete);
        }, cancellationToken: cancellation.Token);

        Assert.Equal(DesktopDispatchStatus.Unknown, result.Dispatch);
        Assert.Equal("DispatchTimeout", result.Error?.Code);
    }
}
