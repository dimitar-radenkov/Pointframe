using Moq;
using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class MicrophoneWarningServiceTests
{
    private readonly Mock<IUserSettingsService> _settings = new();
    private readonly Mock<IMessageBoxService> _messageBox = new();
    private readonly Mock<ITelemetryService> _telemetry = new();
    private readonly UserSettings _current = new() { RecordMicrophone = true };

    public MicrophoneWarningServiceTests()
    {
        _settings.SetupGet(s => s.Current).Returns(_current);
        _settings.Setup(s => s.Update(It.IsAny<Action<UserSettings>>()))
            .Callback<Action<UserSettings>>(mutate => mutate(_current));
    }

    private MicrophoneWarningService CreateService() =>
        new(_settings.Object, _messageBox.Object, _telemetry.Object);

    [Fact]
    public void ReportIfUnavailable_ShowsWarningOnlyOncePerServiceLifetime()
    {
        var service = CreateService();

        service.ReportIfUnavailable(false);
        service.ReportIfUnavailable(false);
        service.ReportIfUnavailable(false);

        _messageBox.Verify(m => m.Confirm(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public void ReportIfUnavailable_TracksTelemetryEveryTimeEvenWhenDialogSuppressed()
    {
        var service = CreateService();

        service.ReportIfUnavailable(false);
        service.ReportIfUnavailable(false);

        _telemetry.Verify(t => t.TrackEvent(TelemetryEvents.MicrophoneUnavailable, null), Times.Exactly(2));
        _messageBox.Verify(m => m.Confirm(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public void ReportIfUnavailable_WhenUserTurnsOff_SavesRecordMicrophoneFalse()
    {
        _messageBox.Setup(m => m.Confirm(It.IsAny<string>(), It.IsAny<string>())).Returns(true);
        var service = CreateService();

        service.ReportIfUnavailable(false);

        _settings.Verify(s => s.Update(It.IsAny<Action<UserSettings>>()), Times.Once);
        Assert.False(_current.RecordMicrophone);
    }

    [Fact]
    public void ReportIfUnavailable_WhenUserKeepsOn_DoesNotChangeSettings()
    {
        _messageBox.Setup(m => m.Confirm(It.IsAny<string>(), It.IsAny<string>())).Returns(false);
        var service = CreateService();

        service.ReportIfUnavailable(false);

        _settings.Verify(s => s.Update(It.IsAny<Action<UserSettings>>()), Times.Never);
        _settings.Verify(s => s.Save(It.IsAny<UserSettings>()), Times.Never);
        Assert.True(_current.RecordMicrophone);
    }

    [Fact]
    public void ReportIfUnavailable_WhenMicrophoneAvailable_DoesNothing()
    {
        var service = CreateService();

        service.ReportIfUnavailable(true);

        _messageBox.Verify(m => m.Confirm(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _telemetry.Verify(t => t.TrackEvent(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>?>()), Times.Never);
    }

    [Fact]
    public void ReportIfUnavailable_WhenSettingOff_DoesNothing()
    {
        _current.RecordMicrophone = false;
        var service = CreateService();

        service.ReportIfUnavailable(false);

        _messageBox.Verify(m => m.Confirm(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _telemetry.Verify(t => t.TrackEvent(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>?>()), Times.Never);
    }
}
