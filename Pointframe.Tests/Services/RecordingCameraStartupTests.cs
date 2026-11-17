using Pointframe.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class RecordingCameraStartupTests
{
    private static IUserSettingsService Settings(bool recordCamera)
    {
        return Mock.Of<IUserSettingsService>(service => service.Current == new UserSettings { RecordCamera = recordCamera });
    }

    [Fact]
    public async Task BeginAsync_WithTheSettingOff_NeverCallsTheFactory()
    {
        var factoryCalls = 0;

        var session = await RecordingCameraStartup.BeginAsync(
            Settings(false),
            () =>
            {
                factoryCalls++;
                return Mock.Of<IRecordingCameraSession>();
            },
            NullLogger.Instance);

        Assert.Null(session);
        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public async Task BeginAsync_WithoutAFactory_ReturnsNull()
    {
        Assert.Null(await RecordingCameraStartup.BeginAsync(Settings(true), null, NullLogger.Instance));
    }

    [Fact]
    public async Task BeginAsync_WithTheSettingOn_StartsTheSessionAndReturnsIt()
    {
        var sessionMock = new Mock<IRecordingCameraSession>();
        sessionMock.Setup(item => item.StartAsync()).Returns(Task.CompletedTask);

        var session = await RecordingCameraStartup.BeginAsync(Settings(true), () => sessionMock.Object, NullLogger.Instance);

        Assert.Same(sessionMock.Object, session);
        sessionMock.Verify(item => item.StartAsync(), Times.Once);
        sessionMock.Verify(item => item.Dispose(), Times.Never);
    }

    [Fact]
    public async Task BeginAsync_WhenTheSessionThrows_DisposesItAndLetsTheRecordingStart()
    {
        var sessionMock = new Mock<IRecordingCameraSession>();
        sessionMock.Setup(item => item.StartAsync()).ThrowsAsync(new InvalidOperationException("boom"));

        var session = await RecordingCameraStartup.BeginAsync(Settings(true), () => sessionMock.Object, NullLogger.Instance);

        Assert.Null(session);
        sessionMock.Verify(item => item.Dispose(), Times.Once);
    }

    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    public void TelemetryValue_ReflectsTheSetting(bool recordCamera, string expected)
    {
        Assert.Equal(expected, RecordingCameraStartup.TelemetryValue(Settings(recordCamera)));
    }
}
