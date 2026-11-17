using Pointframe.Services;
using Moq;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class CameraNoticeServiceTests
{
    private readonly Mock<ITrayIconManager> _tray = new();
    private readonly Mock<ITelemetryService> _telemetry = new();

    private CameraNoticeService CreateService() => new(_tray.Object, _telemetry.Object)
    {
        RunOnUiThread = action => action(),
    };

    [Fact]
    public void ReportUnavailable_ShowsTheBalloonOnlyOncePerRun()
    {
        var service = CreateService();

        service.ReportUnavailable(CameraFailureReasons.Busy, CameraFailureReasons.PhaseStart);
        service.ReportUnavailable(CameraFailureReasons.Lost, CameraFailureReasons.PhaseCapture);
        service.ReportUnavailable(CameraFailureReasons.Stalled, CameraFailureReasons.PhaseCapture);

        _tray.Verify(item => item.ShowTranscriptBalloon(CameraNoticeService.Title, It.IsAny<string>(), true), Times.Once);
    }

    [Fact]
    public void ReportUnavailable_TracksTelemetryEveryTimeWithReasonAndPhaseOnly()
    {
        var service = CreateService();

        service.ReportUnavailable(CameraFailureReasons.NoDevice, CameraFailureReasons.PhaseStart);
        service.ReportUnavailable(CameraFailureReasons.Lost, CameraFailureReasons.PhaseCapture);

        _telemetry.Verify(item => item.TrackEvent(
            TelemetryEvents.CameraUnavailable,
            It.Is<IReadOnlyDictionary<string, string>>(properties =>
                properties.Count == 2
                && properties[TelemetryPropertyKeys.Reason] == "no_device"
                && properties[TelemetryPropertyKeys.Phase] == "start")), Times.Once);
        _telemetry.Verify(item => item.TrackEvent(
            TelemetryEvents.CameraUnavailable,
            It.Is<IReadOnlyDictionary<string, string>>(properties =>
                properties[TelemetryPropertyKeys.Reason] == "lost"
                && properties[TelemetryPropertyKeys.Phase] == "capture")), Times.Once);
    }

    [Fact]
    public void ReportUnavailable_NormalizesUnknownReasonsAndPhasesSoNothingFreeFormIsExported()
    {
        var service = CreateService();

        service.ReportUnavailable("Integrated Webcam (VID_1234)", "weird");

        _telemetry.Verify(item => item.TrackEvent(
            TelemetryEvents.CameraUnavailable,
            It.Is<IReadOnlyDictionary<string, string>>(properties =>
                properties[TelemetryPropertyKeys.Reason] == "other"
                && properties[TelemetryPropertyKeys.Phase] == "start")), Times.Once);
    }

    [Fact]
    public void AccessDenied_NoticePointsToTheWindowsCameraPrivacySettings()
    {
        var service = CreateService();

        service.ReportUnavailable(CameraFailureReasons.AccessDenied, CameraFailureReasons.PhaseStart);

        _tray.Verify(item => item.ShowTranscriptBalloon(
            CameraNoticeService.Title,
            It.Is<string>(message => message.Contains("Let desktop apps access your camera") && !message.Contains("ms-settings")),
            true), Times.Once);
    }

    [Fact]
    public void TheNoticeNeverUsesTheRecordingFailureToastPath()
    {
        var service = CreateService();

        service.ReportUnavailable(CameraFailureReasons.Busy, CameraFailureReasons.PhaseCapture);

        _tray.Verify(item => item.ShowTranscriptBalloon(It.IsAny<string>(), It.IsAny<string>(), true), Times.Once);
        _tray.VerifyNoOtherCalls();
    }

    [Fact]
    public void Catalog_AcceptsTheUnavailableEventAndTheRecordingStartedCameraFlag()
    {
        var unavailable = TelemetryEventCatalog.Validate(
            TelemetryEvents.CameraUnavailable,
            new Dictionary<string, string> { [TelemetryPropertyKeys.Reason] = "busy", [TelemetryPropertyKeys.Phase] = "capture" });
        var started = TelemetryEventCatalog.Validate(
            TelemetryEvents.RecordingStarted,
            new Dictionary<string, string> { [TelemetryPropertyKeys.Type] = "region", [TelemetryPropertyKeys.Camera] = "true" });
        var withDeviceName = TelemetryEventCatalog.Validate(
            TelemetryEvents.CameraUnavailable,
            new Dictionary<string, string>
            {
                [TelemetryPropertyKeys.Reason] = "busy",
                [TelemetryPropertyKeys.Phase] = "capture",
                ["device_name"] = "Integrated Webcam",
            });

        Assert.True(unavailable.IsValid);
        Assert.True(started.IsValid);
        Assert.False(withDeviceName.IsValid);
    }
}
