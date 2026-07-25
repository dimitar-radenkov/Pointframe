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

        sut.TrackCaptureCompleted("copy");
        sut.TrackCaptureCompleted("copy");

        var eventNames = events.Select(item => item.Name).ToList();
        Assert.Equal(2, eventNames.Count(name => name == TelemetryEvents.CaptureCompleted));
        Assert.Equal(1, eventNames.Count(name => name == TelemetryEvents.FirstCaptureCompleted));

        var captureCompleted = events.First(item => item.Name == TelemetryEvents.CaptureCompleted);
        Assert.NotNull(captureCompleted.Props);
        Assert.Equal("copy", captureCompleted.Props![TelemetryPropertyKeys.Action]);

        var firstCapture = events.Single(item => item.Name == TelemetryEvents.FirstCaptureCompleted);
        Assert.NotNull(firstCapture.Props);
        Assert.Equal("screenshot", firstCapture.Props![TelemetryPropertyKeys.CaptureType]);
        Assert.Equal("copy", firstCapture.Props[TelemetryPropertyKeys.FirstAction]);
        Assert.True(firstCapture.Props.ContainsKey(TelemetryPropertyKeys.TimeFromInstallMinutes));
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
        Assert.Equal(2, eventNames.Count(name => name == TelemetryEvents.RecordingCompleted));
        Assert.Equal(1, eventNames.Count(name => name == TelemetryEvents.FirstRecordingCompleted));

        var firstRecording = events.Single(item => item.Name == TelemetryEvents.FirstRecordingCompleted);
        Assert.NotNull(firstRecording.Props);
        Assert.Equal("true", firstRecording.Props![TelemetryPropertyKeys.WithAudio]);
        Assert.Equal("65", firstRecording.Props[TelemetryPropertyKeys.DurationSeconds]);
        Assert.True(firstRecording.Props.ContainsKey(TelemetryPropertyKeys.TimeFromInstallMinutes));
    }
}
