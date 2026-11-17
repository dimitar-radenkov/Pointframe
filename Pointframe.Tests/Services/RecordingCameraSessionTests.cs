using Pointframe.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class RecordingCameraSessionTests
{
    private sealed class FakeCameraService : ICameraCaptureService
    {
        private readonly TaskCompletionSource<string?> _start = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeCameraService(string? startResult = null, bool completeStart = true)
        {
            if (completeStart)
            {
                _start.SetResult(startResult);
            }
        }

        public int StartCalls { get; private set; }

        public int StopCalls { get; private set; }

        public string? LastDeviceId { get; private set; }

        public event Action<CameraFrame>? FrameArrived;

        public event Action<string>? Failed;

        public Task<string?> StartAsync(string? deviceId)
        {
            StartCalls++;
            LastDeviceId = deviceId;
            return _start.Task;
        }

        public Task StopAsync()
        {
            StopCalls++;
            return Task.CompletedTask;
        }

        public void CompleteStart(string? result) => _start.TrySetResult(result);

        public void Emit(CameraFrame frame) => FrameArrived?.Invoke(frame);

        public void Fail(string reason) => Failed?.Invoke(reason);
    }

    private sealed class Harness
    {
        public Harness(UserSettings? settings = null, IReadOnlyList<CameraDevice>? devices = null)
        {
            Settings = settings ?? new UserSettings { RecordCamera = true };
            var settingsService = new Mock<IUserSettingsService>();
            settingsService.SetupGet(service => service.Current).Returns(Settings);
            var deviceService = new Mock<ICameraDeviceService>();
            deviceService
                .Setup(service => service.GetCamerasAsync())
                .ReturnsAsync(devices ?? [new CameraDevice("cam-1", "Front camera")]);
            Devices = deviceService;

            Session = new RecordingCameraSession(
                () =>
                {
                    var service = ServiceFactory();
                    Created.Add(service);
                    return service;
                },
                deviceService.Object,
                settingsService.Object,
                Notice.Object,
                NullLogger<RecordingCameraSession>.Instance)
            {
                StartTimeout = TimeSpan.FromMilliseconds(100),
                StallTimeout = TimeSpan.FromSeconds(2),
                NowMilliseconds = () => Now,
                EnableStallTimer = false,
            };
        }

        public UserSettings Settings { get; }

        public Mock<ICameraDeviceService> Devices { get; }

        public Mock<ICameraNoticeService> Notice { get; } = new();

        public RecordingCameraSession Session { get; }

        public List<FakeCameraService> Created { get; } = [];

        public Func<FakeCameraService> ServiceFactory { get; set; } = () => new FakeCameraService();

        public long Now { get; set; } = 1000;

        public FakeCameraService Service => Created.Single();
    }

    private static CameraFrame NewFrame() => CameraFrame.Rent(4, 4);

    [Fact]
    public async Task StartAsync_WithTheSettingOff_CreatesNoServiceAndTouchesNoCameraApi()
    {
        var harness = new Harness(new UserSettings { RecordCamera = false });

        await harness.Session.StartAsync();

        Assert.Empty(harness.Created);
        Assert.False(harness.Session.IsOn);
        harness.Devices.Verify(service => service.GetCamerasAsync(), Times.Never);
        harness.Notice.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task StartAsync_StartsTheCameraOnTheSystemDefaultWhenNoDeviceIsSaved()
    {
        var harness = new Harness();

        await harness.Session.StartAsync();

        Assert.True(harness.Session.IsOn);
        Assert.Equal(1, harness.Service.StartCalls);
        Assert.Null(harness.Service.LastDeviceId);
        Assert.False(harness.Session.IsBubbleVisible);
    }

    [Fact]
    public async Task StartAsync_UsesTheSavedDeviceIdNotAnIndex()
    {
        var harness = new Harness(new UserSettings { RecordCamera = true, CameraDeviceId = "cam-2" }, [new CameraDevice("cam-1", "A"), new CameraDevice("cam-2", "B")]);

        await harness.Session.StartAsync();

        Assert.Equal("cam-2", harness.Service.LastDeviceId);
    }

    [Fact]
    public async Task StartAsync_WhenTheSavedCameraIsMissing_DoesNotFallBackToAnotherCamera()
    {
        var harness = new Harness(new UserSettings { RecordCamera = true, CameraDeviceId = "gone" }, [new CameraDevice("cam-1", "A")]);

        await harness.Session.StartAsync();

        Assert.Empty(harness.Created);
        Assert.False(harness.Session.IsOn);
        harness.Notice.Verify(notice => notice.ReportUnavailable(CameraFailureReasons.DeviceMissing, CameraFailureReasons.PhaseStart), Times.Once);
    }

    [Fact]
    public async Task StartAsync_WithNoCamera_ReportsNoDeviceAndKeepsTheRecordingGoing()
    {
        var harness = new Harness(devices: []);

        await harness.Session.StartAsync();

        Assert.Empty(harness.Created);
        Assert.False(harness.Session.IsBubbleVisible);
        harness.Notice.Verify(notice => notice.ReportUnavailable(CameraFailureReasons.NoDevice, CameraFailureReasons.PhaseStart), Times.Once);
    }

    [Theory]
    [InlineData(CameraFailureReasons.AccessDenied)]
    [InlineData(CameraFailureReasons.Busy)]
    [InlineData(CameraFailureReasons.NoDevice)]
    [InlineData(CameraFailureReasons.Other)]
    public async Task StartAsync_WhenTheServiceFails_ReportsTheReasonAndReleasesTheCameraOnce(string reason)
    {
        var harness = new Harness { ServiceFactory = () => new FakeCameraService(reason) };

        await harness.Session.StartAsync();

        Assert.False(harness.Session.IsOn);
        Assert.False(harness.Session.IsBubbleVisible);
        Assert.Equal(1, harness.Service.StopCalls);
        harness.Notice.Verify(notice => notice.ReportUnavailable(reason, CameraFailureReasons.PhaseStart), Times.Once);
    }

    [Fact]
    public async Task StartAsync_WhenTheServiceThrows_MapsTheExceptionAndNeverThrows()
    {
        var failing = new Mock<ICameraCaptureService>();
        failing.Setup(service => service.StartAsync(It.IsAny<string?>())).ThrowsAsync(new UnauthorizedAccessException());
        failing.Setup(service => service.StopAsync()).Returns(Task.CompletedTask);
        var harness = new Harness();
        var session = new RecordingCameraSession(
            () => failing.Object,
            harness.Devices.Object,
            Mock.Of<IUserSettingsService>(service => service.Current == harness.Settings),
            harness.Notice.Object,
            NullLogger<RecordingCameraSession>.Instance)
        {
            EnableStallTimer = false,
        };

        await session.StartAsync();

        failing.Verify(service => service.StopAsync(), Times.Once);
        harness.Notice.Verify(notice => notice.ReportUnavailable(CameraFailureReasons.AccessDenied, CameraFailureReasons.PhaseStart), Times.Once);
    }

    [Fact]
    public async Task StartAsync_WhenTheCameraIsLate_ReturnsAfterTheBoundedWaitAndShowsTheBubbleWhenFramesArrive()
    {
        var harness = new Harness { ServiceFactory = () => new FakeCameraService(completeStart: false) };
        var visibilityChanges = 0;
        harness.Session.BubbleVisibilityChanged += () => visibilityChanges++;

        await harness.Session.StartAsync();

        Assert.True(harness.Session.IsOn);
        Assert.False(harness.Session.IsBubbleVisible);

        harness.Service.CompleteStart(null);
        harness.Service.Emit(NewFrame());

        Assert.True(harness.Session.IsBubbleVisible);
        Assert.Equal(1, visibilityChanges);
        Assert.Equal(0, harness.Notice.Invocations.Count);
        harness.Session.TryTakeFrame()?.Dispose();
    }

    [Fact]
    public async Task LateStartFailure_AfterTheBoundedWait_StillReportsAndReleases()
    {
        var harness = new Harness { ServiceFactory = () => new FakeCameraService(completeStart: false) };

        await harness.Session.StartAsync();
        harness.Service.CompleteStart(CameraFailureReasons.Busy);
        await WaitUntilAsync(() => harness.Service.StopCalls == 1);

        Assert.False(harness.Session.IsOn);
        harness.Notice.Verify(notice => notice.ReportUnavailable(CameraFailureReasons.Busy, CameraFailureReasons.PhaseStart), Times.Once);
    }

    [Fact]
    public async Task LateStart_AfterDispose_IsIgnoredAndTheCameraIsStoppedOnce()
    {
        var harness = new Harness { ServiceFactory = () => new FakeCameraService(completeStart: false) };
        await harness.Session.StartAsync();
        var service = harness.Service;

        harness.Session.Dispose();
        service.CompleteStart(null);
        service.Emit(NewFrame());

        Assert.Equal(1, service.StopCalls);
        Assert.False(harness.Session.IsBubbleVisible);
        Assert.Null(harness.Session.TryTakeFrame());
        harness.Notice.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FramesAfterDispose_AreDisposedWithoutRaisingEventsOrThrowing()
    {
        var harness = new Harness();
        await harness.Session.StartAsync();
        var service = harness.Service;
        var raised = false;
        harness.Session.FrameAvailable += () => raised = true;
        harness.Session.BubbleVisibilityChanged += () => raised = true;
        harness.Session.Dispose();

        var frame = NewFrame();
        service.Emit(frame);
        service.Fail(CameraFailureReasons.Lost);

        Assert.True(frame.IsDisposed);
        Assert.False(raised);
        harness.Notice.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Frames_AreLatestOnlyAndSignalOnceUntilTaken()
    {
        var harness = new Harness();
        await harness.Session.StartAsync();
        var signals = 0;
        harness.Session.FrameAvailable += () => signals++;

        var first = NewFrame();
        var second = NewFrame();
        var third = NewFrame();
        harness.Service.Emit(first);
        harness.Service.Emit(second);
        harness.Service.Emit(third);

        Assert.Equal(1, signals);
        Assert.True(first.IsDisposed);
        Assert.True(second.IsDisposed);
        var taken = harness.Session.TryTakeFrame();
        Assert.Same(third, taken);
        Assert.Null(harness.Session.TryTakeFrame());

        harness.Service.Emit(NewFrame());
        Assert.Equal(2, signals);
        taken.Dispose();
        harness.Session.TryTakeFrame()?.Dispose();
    }

    [Fact]
    public async Task CameraLostMidRecording_HidesTheBubbleReleasesTheCameraAndReportsOnce()
    {
        var harness = new Harness();
        await harness.Session.StartAsync();
        harness.Service.Emit(NewFrame());
        var service = harness.Service;
        var hidden = 0;
        harness.Session.BubbleVisibilityChanged += () => hidden++;

        service.Fail(CameraFailureReasons.Lost);
        service.Fail(CameraFailureReasons.Lost);
        await WaitUntilAsync(() => service.StopCalls == 1);

        Assert.False(harness.Session.IsBubbleVisible);
        Assert.False(harness.Session.IsOn);
        Assert.Equal(1, hidden);
        Assert.Equal(1, service.StopCalls);
        harness.Notice.Verify(notice => notice.ReportUnavailable(CameraFailureReasons.Lost, CameraFailureReasons.PhaseCapture), Times.Once);
    }

    [Fact]
    public async Task StalledFrames_HideTheBubbleAndReportOnceWithTheCapturePhase()
    {
        var harness = new Harness();
        await harness.Session.StartAsync();
        harness.Service.Emit(NewFrame());
        Assert.True(harness.Session.IsBubbleVisible);

        harness.Now += 1900;
        harness.Session.CheckStall();
        Assert.True(harness.Session.IsBubbleVisible);

        harness.Now += 200;
        harness.Session.CheckStall();
        harness.Session.CheckStall();

        Assert.False(harness.Session.IsBubbleVisible);
        Assert.True(harness.Session.IsOn);
        harness.Notice.Verify(notice => notice.ReportUnavailable(CameraFailureReasons.Stalled, CameraFailureReasons.PhaseCapture), Times.Once);
        harness.Session.TryTakeFrame()?.Dispose();
    }

    [Fact]
    public async Task FramesResumingAfterAStall_ShowTheBubbleAgainWithoutASecondNotice()
    {
        var harness = new Harness();
        await harness.Session.StartAsync();
        harness.Service.Emit(NewFrame());
        harness.Now += 3000;
        harness.Session.CheckStall();
        Assert.False(harness.Session.IsBubbleVisible);

        harness.Service.Emit(NewFrame());
        Assert.True(harness.Session.IsBubbleVisible);
        harness.Now += 3000;
        harness.Session.CheckStall();

        harness.Notice.Verify(notice => notice.ReportUnavailable(CameraFailureReasons.Stalled, It.IsAny<string>()), Times.Once);
        harness.Session.TryTakeFrame()?.Dispose();
    }

    [Fact]
    public async Task NoFramesEverArriving_ReportsStalledWithTheStartPhase()
    {
        var harness = new Harness();
        await harness.Session.StartAsync();

        harness.Now += 2500;
        harness.Session.CheckStall();

        harness.Notice.Verify(notice => notice.ReportUnavailable(CameraFailureReasons.Stalled, CameraFailureReasons.PhaseStart), Times.Once);
    }

    [Fact]
    public async Task SetOnAsyncFalse_ReleasesTheCameraAndHidesTheBubble_AndTrueStartsItAgain()
    {
        var harness = new Harness();
        await harness.Session.StartAsync();
        harness.Service.Emit(NewFrame());
        var firstService = harness.Created[0];
        var stateChanges = 0;
        harness.Session.StateChanged += () => stateChanges++;

        await harness.Session.SetOnAsync(false);

        Assert.False(harness.Session.IsOn);
        Assert.False(harness.Session.IsBubbleVisible);
        Assert.Equal(1, firstService.StopCalls);

        await harness.Session.SetOnAsync(true);

        Assert.True(harness.Session.IsOn);
        Assert.Equal(2, harness.Created.Count);
        Assert.Equal(1, harness.Created[1].StartCalls);
        Assert.Equal(1, firstService.StopCalls);
        Assert.True(stateChanges >= 2);
        harness.Notice.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SetOnAsyncFalse_Twice_StopsTheCameraOnce()
    {
        var harness = new Harness();
        await harness.Session.StartAsync();

        await harness.Session.SetOnAsync(false);
        await harness.Session.SetOnAsync(false);

        Assert.Equal(1, harness.Service.StopCalls);
    }

    [Fact]
    public async Task Dispose_ReleasesTheCameraExactlyOnce_AndIsIdempotent()
    {
        var harness = new Harness();
        await harness.Session.StartAsync();

        harness.Session.Dispose();
        harness.Session.Dispose();

        Assert.Equal(1, harness.Service.StopCalls);
        Assert.False(harness.Session.IsOn);
    }

    [Fact]
    public async Task Dispose_AfterTheCameraWasToggledOff_DoesNotStopItAgain()
    {
        var harness = new Harness();
        await harness.Session.StartAsync();
        await harness.Session.SetOnAsync(false);

        harness.Session.Dispose();

        Assert.Equal(1, harness.Service.StopCalls);
    }

    [Fact]
    public void Dispose_WithoutEverStarting_DoesNothing()
    {
        var harness = new Harness();

        harness.Session.Dispose();

        Assert.Empty(harness.Created);
    }

    [Fact]
    public async Task SetOnAsyncTrue_AfterDispose_DoesNotCreateACamera()
    {
        var harness = new Harness();
        harness.Session.Dispose();

        await harness.Session.SetOnAsync(true);

        Assert.Empty(harness.Created);
        Assert.False(harness.Session.IsOn);
    }

    [Fact]
    public async Task Dispose_ReleasesAFramePendingInTheMailbox()
    {
        var harness = new Harness();
        await harness.Session.StartAsync();
        var frame = NewFrame();
        harness.Service.Emit(frame);

        harness.Session.Dispose();

        Assert.True(frame.IsDisposed);
    }

    [Fact]
    public async Task ToggleOff_WithAStopThatNeverCompletes_HidesTheBubbleBeforeTheStopFinishes()
    {
        var hung = new HungStopService();
        var harness = new Harness { ServiceFactory = () => throw new InvalidOperationException() };
        var session = new RecordingCameraSession(
            () => hung,
            harness.Devices.Object,
            Mock.Of<IUserSettingsService>(service => service.Current == harness.Settings),
            harness.Notice.Object,
            NullLogger<RecordingCameraSession>.Instance)
        {
            EnableStallTimer = false,
        };
        await session.StartAsync();
        hung.Emit(NewFrame());
        var hidden = false;
        session.BubbleVisibilityChanged += () => hidden = !session.IsBubbleVisible;

        var toggle = session.SetOnAsync(false);

        Assert.False(toggle.IsCompleted);
        Assert.True(hidden);
        Assert.False(session.IsBubbleVisible);
    }

    [Fact]
    public async Task CameraLost_WithAStopThatNeverCompletes_HidesTheBubbleAndReportsBeforeTheStopFinishes()
    {
        var hung = new HungStopService();
        var harness = new Harness();
        var session = new RecordingCameraSession(
            () => hung,
            harness.Devices.Object,
            Mock.Of<IUserSettingsService>(service => service.Current == harness.Settings),
            harness.Notice.Object,
            NullLogger<RecordingCameraSession>.Instance)
        {
            EnableStallTimer = false,
        };
        await session.StartAsync();
        hung.Emit(NewFrame());
        var hiddenEvents = 0;
        session.BubbleVisibilityChanged += () => hiddenEvents++;

        hung.Fail(CameraFailureReasons.Lost);

        Assert.Equal(1, hiddenEvents);
        Assert.False(session.IsBubbleVisible);
        harness.Notice.Verify(notice => notice.ReportUnavailable(CameraFailureReasons.Lost, CameraFailureReasons.PhaseCapture), Times.Once);
    }

    [Fact]
    public async Task ToggleOffAndOn_ReusesTheDeviceIdSnapshottedAtSessionStart()
    {
        var harness = new Harness(new UserSettings { RecordCamera = true, CameraDeviceId = "cam-1" }, [new CameraDevice("cam-1", "A"), new CameraDevice("cam-2", "B")]);
        await harness.Session.StartAsync();

        harness.Settings.CameraDeviceId = "cam-2";
        await harness.Session.SetOnAsync(false);
        await harness.Session.SetOnAsync(true);

        Assert.Equal("cam-1", harness.Created[0].LastDeviceId);
        Assert.Equal("cam-1", harness.Created[1].LastDeviceId);
    }

    private sealed class HungStopService : ICameraCaptureService
    {
        public event Action<CameraFrame>? FrameArrived;

        public event Action<string>? Failed;

        public Task<string?> StartAsync(string? deviceId) => Task.FromResult<string?>(null);

        public Task StopAsync() => new TaskCompletionSource().Task;

        public void Emit(CameraFrame frame) => FrameArrived?.Invoke(frame);

        public void Fail(string reason) => Failed?.Invoke(reason);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }
}
