using Pointframe.Engine.Automation.Models;
using Pointframe.Engine.Automation.Services;
using Pointframe.Mcp;
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

    [Fact]
    public async Task PendingInvokeIsDeliveredAndRepeatedActionIdIsNotReplayed()
    {
        var coordinator = new DesktopActionCoordinator(new DesktopActionLedger(), new DesktopTestReportService());
        var actionId = Guid.NewGuid().ToString();
        var dispatchCount = 0;
        var execution = DesktopTestingMcpTools.CreateSemanticInputExecution(UiInvokeOutcome.Pending, "invoke");

        var first = await coordinator.ExecuteAsync("session-1", actionId, new { elementRef = "element-1" }, _ =>
        {
            dispatchCount++;
            return Task.FromResult(execution);
        });
        var second = await coordinator.ExecuteAsync("session-1", actionId, new { elementRef = "element-1" }, _ =>
        {
            dispatchCount++;
            return Task.FromResult(new DesktopActionExecution(DesktopDispatchStatus.NotStarted));
        });

        Assert.Equal(1, dispatchCount);
        Assert.Equal(DesktopDispatchStatus.Complete, first.Dispatch);
        Assert.Equal("InvokePending", first.Error?.Code);
        Assert.Equal(
            "The invoke was delivered; the app has not returned yet (it may be showing a modal dialog). UI Automation may be unable to inspect the app until the call returns; wait for the dialog to close before observing it again.",
            first.Error?.Message);
        Assert.Equal(first, second);
    }
}
