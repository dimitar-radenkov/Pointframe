using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class RecordingMicrophoneSessionTests
{
    [Fact]
    public void Constructor_WithoutDevice_IsDisabledAndNotToggleable()
    {
        var session = new RecordingMicrophoneSession(
            Mock.Of<IMicrophoneDeviceService>(),
            NullLogger.Instance,
            deviceName: null);

        Assert.False(session.IsEnabled);
        Assert.False(session.CanToggleMute);
        Assert.False(session.InitialMutedState);
    }

    [Fact]
    public void Constructor_WithDevice_CapturesInitialMuteState()
    {
        var microphoneService = Mock.Of<IMicrophoneDeviceService>(service =>
            service.TryGetCaptureDeviceMuted("Studio Mic") == true);

        var session = new RecordingMicrophoneSession(microphoneService, NullLogger.Instance, "Studio Mic");

        Assert.True(session.IsEnabled);
        Assert.True(session.CanToggleMute);
        Assert.True(session.InitialMutedState);
    }

    [Fact]
    public void TrySetMuted_WithoutDevice_ReturnsFalse()
    {
        var session = new RecordingMicrophoneSession(
            Mock.Of<IMicrophoneDeviceService>(),
            NullLogger.Instance,
            deviceName: null);

        Assert.False(session.TrySetMuted(true));
    }

    [Fact]
    public void TrySetMuted_WhenDeviceCallFails_ReturnsFalse()
    {
        var microphoneService = new Mock<IMicrophoneDeviceService>();
        microphoneService.Setup(service => service.TryGetCaptureDeviceMuted("Studio Mic")).Returns(false);
        microphoneService.Setup(service => service.TrySetCaptureDeviceMuted("Studio Mic", true)).Returns(false);

        var session = new RecordingMicrophoneSession(microphoneService.Object, NullLogger.Instance, "Studio Mic");

        Assert.False(session.TrySetMuted(true));
    }

    [Fact]
    public void RestoreInitialMuteState_RestoresCapturedState()
    {
        var microphoneService = new Mock<IMicrophoneDeviceService>();
        microphoneService.Setup(service => service.TryGetCaptureDeviceMuted("Studio Mic")).Returns(false);
        microphoneService.Setup(service => service.TrySetCaptureDeviceMuted("Studio Mic", It.IsAny<bool>())).Returns(true);

        var session = new RecordingMicrophoneSession(microphoneService.Object, NullLogger.Instance, "Studio Mic");
        session.TrySetMuted(true);
        session.RestoreInitialMuteState();

        microphoneService.Verify(service => service.TrySetCaptureDeviceMuted("Studio Mic", false), Times.Once);
    }

    [Fact]
    public void RestoreInitialMuteState_WhenInitialStateUnknown_DoesNothing()
    {
        var microphoneService = new Mock<IMicrophoneDeviceService>();
        microphoneService.Setup(service => service.TryGetCaptureDeviceMuted("Studio Mic")).Returns((bool?)null);

        var session = new RecordingMicrophoneSession(microphoneService.Object, NullLogger.Instance, "Studio Mic");
        session.RestoreInitialMuteState();

        microphoneService.Verify(service => service.TrySetCaptureDeviceMuted(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }
}
