using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Moq;
using Pointframe.Automation.Bridge;
using Pointframe.Services;
using Pointframe.Services.Messaging;
using Xunit;

namespace Pointframe.Tests.Automation;

public sealed class AgentBridgeCommandServiceTests
{
    [Fact]
    public async Task CaptureMonitorAsync_InvokesMonitorLaunchOnDispatcher()
    {
        var dispatcher = new ImmediateDispatcher();
        var captureLaunch = new Mock<ICaptureLaunchService>();
        captureLaunch.Setup(service => service.StartMonitorSnip(@"\\.\DISPLAY1", "agent", It.IsAny<string>())).Returns(true);
        var coordinator = new AgentBridgeSessionCoordinator();
        var metadata = new Mock<IArtifactMetadataService>();
        var sut = new AgentBridgeCommandService(
            dispatcher,
            captureLaunch.Object,
            coordinator,
            metadata.Object,
            Mock.Of<ILogger<AgentBridgeCommandService>>());

        var state = await sut.CaptureMonitorAsync(@"\\.\DISPLAY1");

        Assert.Equal(AgentBridgeOperationStatus.Starting, state.Status);
        captureLaunch.Verify(service => service.StartMonitorSnip(@"\\.\DISPLAY1", "agent", state.OperationId), Times.Once);
    }

    [Fact]
    public async Task SaveOverlayAsync_MatchedSaveCompletion_ReturnsMetadataArtifact()
    {
        var coordinator = new AgentBridgeSessionCoordinator();
        coordinator.TryStartCapture(@"\\.\DISPLAY1", out var operationId);
        var command = new TestCommand();
        coordinator.RegisterActiveSession(new AgentBridgeActiveSession(
            operationId!, @"\\.\DISPLAY1", 1d, 1d, new PixelBounds(0, 0, 100, 100), command));
        var metadataResult = new ImageArtifactMetadata(
            1, "img_1", "image/png", "C:\\capture.png", "abc", 1, DateTimeOffset.UnixEpoch,
            "agent", @"\\.\DISPLAY1", 1d, 1d, new PixelBounds(0, 0, 100, 100));
        var metadata = new Mock<IArtifactMetadataService>();
        metadata.Setup(service => service.WriteImageMetadataAsync(It.IsAny<ImageArtifactMetadataRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(metadataResult);
        var sut = new AgentBridgeCommandService(
            new ImmediateDispatcher(),
            Mock.Of<ICaptureLaunchService>(),
            coordinator,
            metadata.Object,
            Mock.Of<ILogger<AgentBridgeCommandService>>());

        var saveTask = sut.SaveOverlayAsync();
        await sut.HandleCaptureCompletedAsync(new CaptureCompletedMessage("C:\\capture.png", "save"));
        var artifact = await saveTask;

        Assert.Equal(operationId, artifact.OperationId);
        Assert.Equal(metadataResult, artifact.Metadata);
        Assert.Equal(1, command.ExecuteCount);
    }

    private sealed class ImmediateDispatcher : IAgentBridgeDispatcher
    {
        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class TestCommand : ICommand
    {
        public int ExecuteCount { get; private set; }

        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
            ExecuteCount++;
        }
    }
}