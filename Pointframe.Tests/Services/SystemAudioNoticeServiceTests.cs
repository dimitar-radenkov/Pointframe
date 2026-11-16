using Moq;
using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class SystemAudioNoticeServiceTests
{
    private readonly Mock<ITrayIconManager> _tray = new();
    private readonly Mock<ITelemetryService> _telemetry = new();

    private SystemAudioNoticeService CreateService() => new(_tray.Object, _telemetry.Object)
    {
        RunOnUiThread = action => action(),
    };

    [Fact]
    public void ReportUnavailable_ShowsTheBalloonOnlyOncePerRun()
    {
        var service = CreateService();

        service.ReportUnavailable("start_failed", "start");
        service.ReportUnavailable("device_lost", "capture");
        service.ReportUnavailable("mix_failed", "mix");

        _tray.Verify(item => item.ShowTranscriptBalloon(SystemAudioNoticeService.Title, SystemAudioNoticeService.Message, true), Times.Once);
    }

    [Fact]
    public void ReportUnavailable_TracksTelemetryEveryTimeWithReasonAndPhaseOnly()
    {
        var service = CreateService();

        service.ReportUnavailable("start_failed", "start");
        service.ReportUnavailable("mix_failed", "mix");

        _telemetry.Verify(item => item.TrackEvent(
            TelemetryEvents.SystemAudioUnavailable,
            It.Is<IReadOnlyDictionary<string, string>>(properties =>
                properties.Count == 2
                && properties[TelemetryPropertyKeys.Reason] == "start_failed"
                && properties[TelemetryPropertyKeys.Phase] == "start")), Times.Once);
        _telemetry.Verify(item => item.TrackEvent(TelemetryEvents.SystemAudioUnavailable, It.IsAny<IReadOnlyDictionary<string, string>>()), Times.Exactly(2));
    }

    [Fact]
    public void Catalog_AcceptsTheUnavailableEventAndTheRecordingStartedSystemAudioFlag()
    {
        var unavailable = TelemetryEventCatalog.Validate(
            TelemetryEvents.SystemAudioUnavailable,
            new Dictionary<string, string> { [TelemetryPropertyKeys.Reason] = "start_failed", [TelemetryPropertyKeys.Phase] = "start" });
        var started = TelemetryEventCatalog.Validate(
            TelemetryEvents.RecordingStarted,
            new Dictionary<string, string> { [TelemetryPropertyKeys.Type] = "region", [TelemetryPropertyKeys.SystemAudio] = "true" });

        Assert.True(unavailable.IsValid);
        Assert.True(started.IsValid);
    }
}
