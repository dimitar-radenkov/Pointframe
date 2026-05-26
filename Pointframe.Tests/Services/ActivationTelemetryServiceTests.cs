using Moq;
using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class ActivationTelemetryServiceTests
{
    [Fact]
    public void TrackCaptureCompleted_TracksFirstCaptureOnlyOnce()
    {
        var events = new List<(string Name, IReadOnlyDictionary<string, string>? Props)>();
        var telemetryMock = new Mock<ITelemetryService>();
        telemetryMock
            .Setup(service => service.TrackEvent(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .Callback<string, IReadOnlyDictionary<string, string>?>((name, props) => events.Add((name, props)));

        var settings = new UserSettings
        {
            InstallCreatedUtc = DateTime.UtcNow.AddMinutes(-20),
            RecordMicrophone = true,
        };

        var settingsMock = new Mock<IUserSettingsService>();
        settingsMock.SetupGet(service => service.Current).Returns(() => settings);
        settingsMock
            .Setup(service => service.Update(It.IsAny<Action<UserSettings>>()))
            .Callback<Action<UserSettings>>(mutate => mutate(settings));

        var sut = new ActivationTelemetryService(telemetryMock.Object, settingsMock.Object);

        sut.TrackCaptureCompleted();
        sut.TrackCaptureCompleted();

        var eventNames = events.Select(item => item.Name).ToList();
        Assert.Equal(2, eventNames.Count(name => name == "capture_completed"));
        Assert.Equal(1, eventNames.Count(name => name == "first_capture_completed"));

        var firstCapture = events.Single(item => item.Name == "first_capture_completed");
        Assert.NotNull(firstCapture.Props);
        Assert.Equal("screenshot", firstCapture.Props!["capture_type"]);
        Assert.True(firstCapture.Props.ContainsKey("time_from_install_minutes"));
    }

    [Fact]
    public void TrackRecordingCompleted_TracksFirstRecordingOnlyOnceAndIncludesDuration()
    {
        var events = new List<(string Name, IReadOnlyDictionary<string, string>? Props)>();
        var telemetryMock = new Mock<ITelemetryService>();
        telemetryMock
            .Setup(service => service.TrackEvent(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .Callback<string, IReadOnlyDictionary<string, string>?>((name, props) => events.Add((name, props)));

        var settings = new UserSettings
        {
            InstallCreatedUtc = DateTime.UtcNow.AddMinutes(-45),
            RecordMicrophone = true,
        };

        var settingsMock = new Mock<IUserSettingsService>();
        settingsMock.SetupGet(service => service.Current).Returns(() => settings);
        settingsMock
            .Setup(service => service.Update(It.IsAny<Action<UserSettings>>()))
            .Callback<Action<UserSettings>>(mutate => mutate(settings));

        var sut = new ActivationTelemetryService(telemetryMock.Object, settingsMock.Object);

        sut.TrackRecordingCompleted("01:05");
        sut.TrackRecordingCompleted("01:05");

        var eventNames = events.Select(item => item.Name).ToList();
        Assert.Equal(2, eventNames.Count(name => name == "recording_completed"));
        Assert.Equal(1, eventNames.Count(name => name == "first_recording_completed"));

        var firstRecording = events.Single(item => item.Name == "first_recording_completed");
        Assert.NotNull(firstRecording.Props);
        Assert.Equal("true", firstRecording.Props!["with_audio"]);
        Assert.Equal("65", firstRecording.Props["duration_seconds"]);
        Assert.True(firstRecording.Props.ContainsKey("time_from_install_minutes"));
    }
}
