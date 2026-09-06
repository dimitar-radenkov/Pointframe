using System.Windows.Input;
using Pointframe.Automation.Bridge;
using Xunit;

namespace Pointframe.Tests.Automation;

public sealed class AgentBridgeSessionCoordinatorTests
{
    [Fact]
    public void RegisterActiveSession_AfterStartedCapture_ExposesAnnotatingState()
    {
        var sut = new AgentBridgeSessionCoordinator();
        Assert.True(sut.TryStartCapture(@"\\.\DISPLAY1", out var operationId));
        var command = new TestCommand(canExecute: true);

        sut.RegisterActiveSession(new AgentBridgeActiveSession(
            operationId!, @"\\.\DISPLAY1", 1.5d, 1.5d, new PixelBounds(0, 0, 2560, 1440), command));

        var state = sut.GetState();
        Assert.Equal(AgentBridgeOperationStatus.Annotating, state.Status);
        Assert.Equal(operationId, state.OperationId);
        Assert.True(state.CanSave);
        Assert.Equal(new PixelBounds(0, 0, 2560, 1440), state.CaptureBoundsPixels);
    }

    [Fact]
    public void TryStartCapture_WhenAnotherCaptureIsActive_RejectsRequest()
    {
        var sut = new AgentBridgeSessionCoordinator();
        Assert.True(sut.TryStartCapture(@"\\.\DISPLAY1", out _));

        var started = sut.TryStartCapture(@"\\.\DISPLAY2", out var operationId);

        Assert.False(started);
        Assert.Null(operationId);
    }

    [Fact]
    public void TryBeginSaving_WhenActiveSessionCanSave_MovesToSavingAndReturnsCommand()
    {
        var sut = new AgentBridgeSessionCoordinator();
        sut.TryStartCapture(@"\\.\DISPLAY1", out var operationId);
        var command = new TestCommand(canExecute: true);
        sut.RegisterActiveSession(new AgentBridgeActiveSession(
            operationId!, @"\\.\DISPLAY1", 1d, 1d, new PixelBounds(0, 0, 1, 1), command));

        var started = sut.TryBeginSaving(out var session);

        Assert.True(started);
        Assert.Same(command, session!.SaveCommand);
        Assert.Equal(AgentBridgeOperationStatus.Saving, sut.GetState().Status);
    }

    [Fact]
    public void ClearSession_WhileSaving_PreservesSessionUntilCompletion()
    {
        var sut = new AgentBridgeSessionCoordinator();
        sut.TryStartCapture(@"\\.\DISPLAY1", out var operationId);
        var command = new TestCommand(canExecute: true);
        sut.RegisterActiveSession(new AgentBridgeActiveSession(
            operationId!, @"\\.\DISPLAY1", 1d, 1d, new PixelBounds(0, 0, 1, 1), command));
        sut.TryBeginSaving(out _);

        sut.ClearSession(operationId!);

        Assert.True(sut.TryComplete(operationId!, out var completedSession));
        Assert.Same(command, completedSession!.SaveCommand);
        Assert.Equal(AgentBridgeOperationStatus.Completed, sut.GetState().Status);
    }

    private sealed class TestCommand(bool canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => canExecute;

        public void Execute(object? parameter)
        {
        }
    }
}