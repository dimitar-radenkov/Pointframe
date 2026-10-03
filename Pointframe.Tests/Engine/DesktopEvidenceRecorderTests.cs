using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using Moq;
using Pointframe.Engine;
using Pointframe.Engine.Automation.Models;
using Pointframe.Engine.Automation.Services;
using Xunit;

namespace Pointframe.Tests.Engine;

public sealed class DesktopEvidenceRecorderTests : IDisposable
{
    private const int TargetProcessId = 4242;

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pointframe-evidence-{Guid.NewGuid():N}");
    private readonly Mock<IDisplayCaptureEngine> _capture = new();
    private readonly Mock<IWindowDiscoveryService> _windows = new();
    private readonly DesktopProcessIdentity _process = new("process-1", TargetProcessId, DateTimeOffset.UtcNow, "target.exe", "hash");

    public DesktopEvidenceRecorderTests()
    {
        Directory.CreateDirectory(_root);
        _capture
            .Setup(engine => engine.Capture(It.IsAny<PixelBounds>()))
            .Returns((PixelBounds bounds) => new Bitmap(bounds.Width, bounds.Height));
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void NoneModeHasNoDirectoryAndNeverCaptures()
    {
        var recorder = CreateRecorder();

        Assert.Null(recorder.BeginSession("session-1", _root, DesktopEvidenceMode.None));
        Assert.False(recorder.ShouldCapture("session-1", isFailure: true));
        Assert.Equal("EvidenceDisabled", recorder.Capture("session-1", _process, "check").Error);
    }

    [Fact]
    public void FailuresModeCapturesOnlyFailures()
    {
        var recorder = CreateRecorder();
        recorder.BeginSession("session-1", _root, DesktopEvidenceMode.Failures);

        Assert.True(recorder.ShouldCapture("session-1", isFailure: true));
        Assert.False(recorder.ShouldCapture("session-1", isFailure: false));
    }

    [Fact]
    public void UnknownSessionNeverCaptures()
    {
        Assert.False(CreateRecorder().ShouldCapture("session-unknown", isFailure: true));
    }

    [Fact]
    public void CaptureCoversOnlyTheTargetsVisibleWindowsAndHashesTheFile()
    {
        SetupWindows(
            Window(TargetProcessId, new PixelBounds(100, 100, 200, 100)),
            Window(TargetProcessId, new PixelBounds(250, 150, 100, 100)),
            Window(TargetProcessId, new PixelBounds(0, 0, 50, 50), isMinimized: true),
            Window(9999, new PixelBounds(0, 0, 1000, 1000)));
        var recorder = CreateRecorder();
        var directory = recorder.BeginSession("session-1", _root, DesktopEvidenceMode.All)!;

        var evidence = recorder.Capture("session-1", _process, "check");

        Assert.Null(evidence.Error);
        Assert.Equal(new PixelBounds(100, 100, 250, 150), evidence.BoundsPixels);
        Assert.Equal("0001-check.png", evidence.Path);
        var bytes = File.ReadAllBytes(Path.Combine(directory, evidence.Path!));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), evidence.Sha256);
        _capture.Verify(engine => engine.Capture(new PixelBounds(100, 100, 250, 150)), Times.Once);
    }

    [Fact]
    public void SequentialCapturesGetIncreasingNames()
    {
        SetupWindows(Window(TargetProcessId, new PixelBounds(0, 0, 10, 10)));
        var recorder = CreateRecorder();
        recorder.BeginSession("session-1", _root, DesktopEvidenceMode.All);

        recorder.Capture("session-1", _process, "action");
        var second = recorder.Capture("session-1", _process, "check");

        Assert.Equal("0002-check.png", second.Path);
    }

    [Fact]
    public void TargetWithoutVisibleWindowRecordsTheGap()
    {
        SetupWindows(Window(9999, new PixelBounds(0, 0, 10, 10)));
        var recorder = CreateRecorder();
        recorder.BeginSession("session-1", _root, DesktopEvidenceMode.All);

        var evidence = recorder.Capture("session-1", _process, "check");

        Assert.Equal("NoVisibleWindow", evidence.Error);
        Assert.Null(evidence.Path);
        _capture.Verify(engine => engine.Capture(It.IsAny<PixelBounds>()), Times.Never);
    }

    [Fact]
    public void CaptureFailureIsRecordedRatherThanThrown()
    {
        SetupWindows(Window(TargetProcessId, new PixelBounds(0, 0, 10, 10)));
        _capture.Setup(engine => engine.Capture(It.IsAny<PixelBounds>())).Throws(new InvalidOperationException("no desktop"));
        var recorder = CreateRecorder();
        recorder.BeginSession("session-1", _root, DesktopEvidenceMode.All);

        var evidence = recorder.Capture("session-1", _process, "check");

        Assert.Equal("CaptureFailed", evidence.Error);
        Assert.Equal(new PixelBounds(0, 0, 10, 10), evidence.BoundsPixels);
    }

    [Fact]
    public async Task ActionEvidenceAttachesOnceToTheRecordedAction()
    {
        var reports = new DesktopTestReportService();
        var actionId = Guid.NewGuid().ToString();
        reports.RecordAction("session-1", new DesktopTestActionReport(
            actionId,
            "desktop action",
            new DesktopActionResult(
                DesktopTestingLimits.SchemaVersion,
                actionId,
                DesktopOperationStatus.Completed,
                DesktopDispatchStatus.Complete,
                DesktopVerificationStatus.NotRequested,
                DesktopObservationStatus.NotRequested),
            DateTimeOffset.UtcNow));
        var evidence = new DesktopTestEvidence("0001-action.png", "ABC", null, DateTimeOffset.UtcNow);

        Assert.True(reports.NeedsActionEvidence("session-1", actionId));
        reports.AttachActionEvidence("session-1", actionId, evidence);

        Assert.False(reports.NeedsActionEvidence("session-1", actionId));
        Assert.Same(evidence, (await reports.FinalizeAsync("session-1")).Actions[0].Evidence);
    }

    private DesktopEvidenceRecorder CreateRecorder() => new(_capture.Object, _windows.Object);

    private void SetupWindows(params WindowDescriptor[] windows) =>
        _windows.Setup(discovery => discovery.GetWindows()).Returns(windows);

    private static WindowDescriptor Window(int processId, PixelBounds bounds, bool isMinimized = false) =>
        new(processId * 10L, "window", "process", processId, bounds, null, isMinimized);
}
