using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Pointframe.Engine;
using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class ScreenRecordingServiceSystemAudioTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly FakeRemuxer _remuxer = new();
    private readonly FakeCapture _capture = new();
    private readonly FakeMixer _mixer = new();
    private readonly Mock<ISystemAudioNoticeService> _notice = new();
    private readonly UserSettings _settings = new() { RecordMicrophone = false };
    private string? _writerMicrophone = "unset";
    private int _factoryCalls;

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Start_WhenSettingIsOff_DoesNotTouchTheCaptureService()
    {
        var service = CreateService();
        service.Start(0, 0, 100, 100, _folder.File("off.mp4"));

        service.Stop();

        Assert.Equal(0, _factoryCalls);
        Assert.Equal(0, _capture.StartCalls);
        Assert.Empty(_mixer.Calls);
        Assert.False(service.IsRecordingSystemAudio);
        Assert.Equal(RecordingStopStatus.Complete, service.LastStopResult.Status);
        _notice.Verify(item => item.ReportUnavailable(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void Stop_WhenSettingIsOnAndMicrophoneOff_MixesTheSystemAudioIntoTheFinishedVideo()
    {
        _settings.RecordSystemAudio = true;
        var service = CreateService();
        service.Start(0, 0, 100, 100, _folder.File("sys.mp4"));
        Assert.True(service.IsRecordingSystemAudio);
        Assert.Equal(1, _capture.BeginTimelineCalls);

        service.Stop();

        var call = Assert.Single(_mixer.Calls);
        Assert.Equal(_folder.File("sys.mp4"), call.Video);
        Assert.Equal(_folder.File(".sys.sysaudio.wav"), call.Wav);
        Assert.False(call.VideoHasMicrophone);
        Assert.Equal(RecordingStopStatus.Complete, service.LastStopResult.Status);
        Assert.False(File.Exists(call.Wav));
        Assert.False(service.IsRecordingSystemAudio);
    }

    [Fact]
    public void Stop_WhenSettingIsOnAndMicrophoneRecorded_AsksForAMixOfBothTracks()
    {
        _settings.RecordSystemAudio = true;
        _settings.RecordMicrophone = true;
        var service = CreateService(microphoneName: "USB Mic");
        service.Start(0, 0, 100, 100, _folder.File("both.mp4"));

        service.Stop();

        Assert.Equal("USB Mic", _writerMicrophone);
        Assert.True(Assert.Single(_mixer.Calls).VideoHasMicrophone);
    }

    [Fact]
    public void Start_WhenLoopbackCannotStart_RecordsWithoutSystemAudioAndNotifiesOnce()
    {
        _settings.RecordSystemAudio = true;
        _capture.StartFailure = "start_failed";
        var service = CreateService();

        service.Start(0, 0, 100, 100, _folder.File("nostart.mp4"));
        Assert.True(service.IsRecording);
        Assert.False(service.IsRecordingSystemAudio);
        service.Stop();

        Assert.Equal(RecordingStopStatus.Complete, service.LastStopResult.Status);
        Assert.True(File.Exists(_folder.File("nostart.mp4")));
        Assert.Empty(_mixer.Calls);
        _notice.Verify(item => item.ReportUnavailable("start_failed", "start"), Times.Once);
        _notice.Verify(item => item.ReportUnavailable(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public void Stop_WhenLoopbackFailsMidRecording_KeepsTheVideoNotifiesOnceAndDeletesTheWav()
    {
        _settings.RecordSystemAudio = true;
        var service = CreateService();
        service.Start(0, 0, 100, 100, _folder.File("midfail.mp4"));
        var wavPath = _folder.File(".midfail.sysaudio.wav");
        Assert.True(File.Exists(wavPath));

        _capture.RaiseFailure("device_lost");
        service.Stop();

        Assert.Equal(RecordingStopStatus.Complete, service.LastStopResult.Status);
        Assert.True(File.Exists(_folder.File("midfail.mp4")));
        Assert.Empty(_mixer.Calls);
        Assert.False(File.Exists(wavPath));
        _notice.Verify(item => item.ReportUnavailable("device_lost", "capture"), Times.Once);
        _notice.Verify(item => item.ReportUnavailable(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public void Stop_WhenTheMixFails_KeepsTheOriginalVideoAsTheSavedResultAndDeletesTheWav()
    {
        _settings.RecordSystemAudio = true;
        _mixer.Result = false;
        var service = CreateService();
        service.Start(0, 0, 100, 100, _folder.File("mixfail.mp4"));

        service.Stop();

        Assert.Equal(RecordingStopStatus.Complete, service.LastStopResult.Status);
        Assert.Equal(_folder.File("mixfail.mp4"), service.LastStopResult.ArtifactPath);
        Assert.True(File.Exists(_folder.File("mixfail.mp4")));
        Assert.False(File.Exists(_folder.File(".mixfail.sysaudio.wav")));
        _notice.Verify(item => item.ReportUnavailable("mix_failed", "mix"), Times.Once);
    }

    [Fact]
    public void Stop_WhenTheMixThrows_StillReportsACompleteRecording()
    {
        _settings.RecordSystemAudio = true;
        _mixer.Throws = true;
        var service = CreateService();
        service.Start(0, 0, 100, 100, _folder.File("mixthrow.mp4"));

        service.Stop();

        Assert.Equal(RecordingStopStatus.Complete, service.LastStopResult.Status);
        Assert.False(File.Exists(_folder.File(".mixthrow.sysaudio.wav")));
        _notice.Verify(item => item.ReportUnavailable("mix_failed", "mix"), Times.Once);
    }

    [Fact]
    public void Stop_WhenTheVideoCannotBeRemuxed_DeletesTheWavAndDoesNotMix()
    {
        _settings.RecordSystemAudio = true;
        _remuxer.RemuxResult = null;
        var service = CreateService();
        service.Start(0, 0, 100, 100, _folder.File("partial.mp4"));

        service.Stop();

        Assert.Equal(RecordingStopStatus.Partial, service.LastStopResult.Status);
        Assert.Empty(_mixer.Calls);
        Assert.False(File.Exists(_folder.File(".partial.sysaudio.wav")));
    }

    [Fact]
    public void PauseAndResume_AreForwardedToTheCaptureSoPausedSpansStayOutOfTheAudio()
    {
        _settings.RecordSystemAudio = true;
        var service = CreateService();
        service.Start(0, 0, 100, 100, _folder.File("pause.mp4"));

        service.Pause();
        service.Resume();
        service.Stop();

        Assert.Equal(1, _capture.PauseCalls);
        Assert.Equal(1, _capture.ResumeCalls);
    }

    private ScreenRecordingService CreateService(string? microphoneName = null)
    {
        var factory = new Mock<IVideoWriterFactory>();
        factory
            .Setup(item => item.Create(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns<int, int, int, string, string?>((_, _, _, output, microphone) =>
            {
                _writerMicrophone = microphone;
                return new Writer(output);
            });
        var microphones = new Mock<IMicrophoneDeviceService>();
        if (microphoneName is not null)
        {
            microphones.Setup(item => item.GetDefaultCaptureDeviceName()).Returns(microphoneName);
            microphones.Setup(item => item.GetAvailableCaptureDeviceNames()).Returns([microphoneName]);
        }

        var settings = new Mock<IUserSettingsService>();
        settings.SetupGet(item => item.Current).Returns(_settings);
        return new ScreenRecordingService(
            NullLogger<ScreenRecordingService>.Instance,
            microphones.Object,
            settings.Object,
            factory.Object,
            new IdleFrameCapture(),
            null,
            new RecordingRecovery(_remuxer, NullLogger<RecordingRecovery>.Instance),
            () =>
            {
                _factoryCalls++;
                return _capture;
            },
            _mixer,
            _notice.Object);
    }

    private sealed class IdleFrameCapture : IRawFrameCapture
    {
        public void Capture(byte[] frameData) => Array.Clear(frameData);

        public void Dispose()
        {
        }
    }

    private sealed class Writer : IVideoWriter, IRecoverableVideoWriter
    {
        public Writer(string outputPath)
        {
            IntermediatePath = RecordingRecovery.GetIntermediatePath(outputPath);
            FragmentedMp4Fixture.Write(IntermediatePath, withFragment: true);
        }

        public string IntermediatePath { get; }

        public void WriteFrame(byte[] frameData)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeCapture : ISystemAudioCaptureService
    {
        private string? _wavPath;
        private string? _failure;

        public string? StartFailure { get; set; }
        public int StartCalls { get; private set; }
        public int PauseCalls { get; private set; }
        public int ResumeCalls { get; private set; }
        public bool IsCapturing { get; private set; }
        public event Action<string>? CaptureFailed;

        public bool TryStart(string wavPath, out string? failureReason)
        {
            StartCalls++;
            failureReason = StartFailure;
            if (StartFailure is not null)
            {
                return false;
            }

            _wavPath = wavPath;
            _failure = null;
            File.WriteAllBytes(wavPath, [0, 1, 2]);
            IsCapturing = true;
            return true;
        }

        public int BeginTimelineCalls { get; private set; }

        public void BeginTimeline() => BeginTimelineCalls++;

        public void Pause() => PauseCalls++;

        public void Resume() => ResumeCalls++;

        public void RaiseFailure(string reason)
        {
            _failure = reason;
            CaptureFailed?.Invoke(reason);
        }

        public SystemAudioStopResult Stop()
        {
            IsCapturing = false;
            if (_failure is not null)
            {
                return new SystemAudioStopResult(null, _failure);
            }

            return new SystemAudioStopResult(_wavPath, null);
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeMixer : ISystemAudioMixer
    {
        public bool Result { get; set; } = true;
        public bool Throws { get; set; }
        public List<(string Video, string Wav, bool VideoHasMicrophone)> Calls { get; } = [];

        public bool TryMix(string videoPath, string wavPath, bool videoHasMicrophone)
        {
            Calls.Add((videoPath, wavPath, videoHasMicrophone));
            if (Throws)
            {
                throw new InvalidOperationException("mix exploded");
            }

            return Result;
        }
    }
}
