using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Pointframe.Engine;
using Pointframe.Services;
using Xunit;
using Xunit.Abstractions;

namespace Pointframe.Tests.Services;

[Collection("FfmpegPathOverride")]
public sealed class RecordingRecoveryRealWriterTests : IDisposable
{
    private const int Fps = 30;

    private readonly TempFolder _folder = new();
    private readonly List<IReadOnlyDictionary<string, string>> _failures = [];
    private readonly ITestOutputHelper _output;

    public RecordingRecoveryRealWriterTests(ITestOutputHelper output) => _output = output;

    public void Dispose() => _folder.Dispose();

    [Fact]
    [Trait("Category", "Integration")]
    public void RecordingRecovery_RealBrokenPipe_ReportsPartialEncodePipeBrokenAndSeekableFile()
    {
        if (Skipped(RealRecordingPrerequisites.FfmpegSkipReason))
        {
            return;
        }

        var output = _folder.File("pipe.mp4");
        var capture = new CountingFrameCapture();
        var (service, writers) = CreateService(microphoneDevice: null, capture);
        service.Start(0, 0, 64, 64, output);

        Thread.Sleep(TimeSpan.FromSeconds(4));
        var capturedAtKill = capture.Count;
        KillFfmpeg(writers.Single());
        Thread.Sleep(TimeSpan.FromSeconds(1));
        service.Stop();

        Assert.Equal(RecordingStopStatus.Partial, service.LastStopResult.Status);
        Assert.Equal(output, service.LastStopResult.ArtifactPath);
        var failure = Assert.Single(_failures);
        Assert.Equal("encode", failure[TelemetryPropertyKeys.Phase]);
        Assert.Equal("pipe_broken", failure[TelemetryPropertyKeys.Reason]);
        Assert.Equal("true", failure[TelemetryPropertyKeys.Recovered]);
        var expectedSeconds = capturedAtKill / (double)Fps;
        Assert.InRange(service.LastStopResult.Duration.TotalSeconds, expectedSeconds - 1.5, expectedSeconds + 1.5);
        Assert.False(File.Exists(RecordingRecovery.GetIntermediatePath(output)));
        RecordingRecoveryIntegrationTests.AssertSeekable(output, seekSeconds: 2);
        Assert.Equal(0, RecordingRecoveryIntegrationTests.Ffmpeg("-v", "error", "-i", output, "-f", "null", "-").ExitCode);
        service.Dispose();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void RecordingRecovery_RealMicrophoneCleanStopWithPause_ProducesFaststartH264AacMp4()
    {
        if (Skipped(RealRecordingPrerequisites.MicrophoneSkipReason))
        {
            return;
        }

        var output = _folder.File("mic-clean.mp4");
        var (service, _) = CreateService(RealRecordingPrerequisites.Device, new CountingFrameCapture());
        service.Start(0, 0, 64, 64, output);

        Thread.Sleep(TimeSpan.FromSeconds(1.5));
        service.Pause();
        Assert.True(service.IsPaused);
        Thread.Sleep(TimeSpan.FromSeconds(1));
        service.Resume();
        Assert.False(service.IsPaused);
        Thread.Sleep(TimeSpan.FromSeconds(1.5));
        service.Stop();

        Assert.Equal(RecordingStopStatus.Complete, service.LastStopResult.Status);
        Assert.Equal(output, service.LastStopResult.ArtifactPath);
        Assert.Empty(_failures);
        Assert.InRange(service.LastStopResult.Duration.TotalSeconds, 4 - 1.5, 4 + 1.5);
        Assert.False(File.Exists(RecordingRecovery.GetIntermediatePath(output)));
        RecordingRecoveryIntegrationTests.AssertFaststart(output);
        var info = RecordingRecoveryIntegrationTests.Ffmpeg("-hide_banner", "-i", output).Stderr;
        Assert.Contains("Video: h264", info);
        Assert.Contains("Audio: aac", info);
        RecordingRecoveryIntegrationTests.AssertSeekable(output, seekSeconds: 2);
        service.Dispose();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void RecordingRecovery_RealMicrophoneKill_RecoversAacStreamAndSeekableFile()
    {
        if (Skipped(RealRecordingPrerequisites.MicrophoneSkipReason))
        {
            return;
        }

        var output = _folder.File("mic-killed.mp4");
        var (service, writers) = CreateService(RealRecordingPrerequisites.Device, new CountingFrameCapture());
        service.Start(0, 0, 64, 64, output);

        var encodedSeconds = WaitForEncodedSeconds(writers.Single(), minimumSeconds: 3, timeout: TimeSpan.FromSeconds(20));
        Assert.True(encodedSeconds >= 3, $"ffmpeg encoded only {encodedSeconds:0.00} s within the timeout although the microphone prerequisite passed.");
        _output.WriteLine($"Encoded length at kill: {encodedSeconds:0.00} s");
        KillFfmpeg(writers.Single());
        Thread.Sleep(TimeSpan.FromSeconds(1));
        service.Stop();

        Assert.Equal(RecordingStopStatus.Partial, service.LastStopResult.Status);
        Assert.Equal(output, service.LastStopResult.ArtifactPath);
        var failure = Assert.Single(_failures);
        Assert.Equal("true", failure[TelemetryPropertyKeys.Recovered]);
        var expectedSeconds = encodedSeconds;
        Assert.InRange(service.LastStopResult.Duration.TotalSeconds, expectedSeconds - 1.5, expectedSeconds + 1.5);
        Assert.False(File.Exists(RecordingRecovery.GetIntermediatePath(output)));
        var info = RecordingRecoveryIntegrationTests.Ffmpeg("-hide_banner", "-i", output).Stderr;
        Assert.Contains("Video: h264", info);
        Assert.Contains("Audio: aac", info);
        RecordingRecoveryIntegrationTests.AssertSeekable(output, seekSeconds: 1);
        service.Dispose();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RecordingRecovery_CleanlyStoppedRealOutput_TrimsAndExportsGif()
    {
        if (Skipped(RealRecordingPrerequisites.FfmpegSkipReason))
        {
            return;
        }

        var output = _folder.File("source.mp4");
        var (service, _) = CreateService(microphoneDevice: null, new CountingFrameCapture());
        service.Start(0, 0, 64, 64, output);
        Thread.Sleep(TimeSpan.FromSeconds(4));
        service.Stop();
        Assert.Equal(RecordingStopStatus.Complete, service.LastStopResult.Status);

        var trimmed = _folder.File("trimmed.mp4");
        await new VideoTrimService(NullLogger<VideoTrimService>.Instance)
            .Trim(output, trimmed, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));
        var gif = _folder.File("export.gif");
        await new GifExportService(NullLogger<GifExportService>.Instance).Export(output, gif, 10);

        var trimmedDuration = new FfmpegRecordingRemuxer(NullLogger<FfmpegRecordingRemuxer>.Instance).ReadDuration(trimmed);
        Assert.NotNull(trimmedDuration);
        Assert.InRange(trimmedDuration.Value.TotalSeconds, 1.0, 3.0);
        RecordingRecoveryIntegrationTests.AssertSeekable(trimmed, seekSeconds: 1);
        var header = new byte[6];
        using (var stream = File.OpenRead(gif))
        {
            stream.ReadExactly(header);
            Assert.True(stream.Length > 100);
        }

        Assert.Equal("GIF89a", System.Text.Encoding.ASCII.GetString(header));
        service.Dispose();
    }

    private bool Skipped(string? reason)
    {
        if (reason is null)
        {
            return false;
        }

        _output.WriteLine($"SKIPPED: {reason}");
        return true;
    }

    private (ScreenRecordingService Service, List<FFMpegVideoWriter> Writers) CreateService(string? microphoneDevice, IRawFrameCapture capture)
    {
        var writers = new List<FFMpegVideoWriter>();
        var factory = new Mock<IVideoWriterFactory>();
        factory
            .Setup(item => item.Create(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns<int, int, int, string, string?>((width, height, fps, output, microphone) =>
            {
                var writer = new FFMpegVideoWriter(width, height, fps, output, NullLogger.Instance, microphone);
                writers.Add(writer);
                return writer;
            });

        var microphone = new Mock<IMicrophoneDeviceService>();
        if (microphoneDevice is not null)
        {
            microphone.Setup(item => item.GetAvailableCaptureDeviceNames()).Returns([microphoneDevice]);
            microphone.Setup(item => item.GetDefaultCaptureDeviceName()).Returns(microphoneDevice);
        }
        else
        {
            microphone.Setup(item => item.GetAvailableCaptureDeviceNames()).Returns([]);
        }

        var settings = new UserSettings
        {
            RecordingFps = Fps,
            RecordMicrophone = microphoneDevice is not null,
            RecordingMicrophoneDeviceName = microphoneDevice,
        };
        var telemetry = new Mock<ITelemetryService>();
        telemetry
            .Setup(item => item.TrackEvent(TelemetryEvents.RecordingFailed, It.IsAny<IReadOnlyDictionary<string, string>>()))
            .Callback<string, IReadOnlyDictionary<string, string>?>((_, properties) => _failures.Add(properties!));

        var service = new ScreenRecordingService(
            NullLogger<ScreenRecordingService>.Instance,
            microphone.Object,
            Mock.Of<IUserSettingsService>(item => item.Current == settings),
            factory.Object,
            capture,
            telemetry.Object,
            new RecordingRecovery(new FfmpegRecordingRemuxer(NullLogger<FfmpegRecordingRemuxer>.Instance), NullLogger<RecordingRecovery>.Instance));
        return (service, writers);
    }

    private static double WaitForEncodedSeconds(FFMpegVideoWriter writer, double minimumSeconds, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var encoded = 0d;
        while (DateTime.UtcNow < deadline)
        {
            var matches = Regex.Matches(writer.RecentStandardError, @"frame=\s*\d+.*?time=(\d+):(\d+):(\d+(?:\.\d+)?)");
            if (matches.Count > 0)
            {
                var last = matches[^1];
                encoded = (int.Parse(last.Groups[1].Value) * 3600) + (int.Parse(last.Groups[2].Value) * 60) + double.Parse(last.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (encoded >= minimumSeconds)
                {
                    return encoded;
                }
            }

            Thread.Sleep(100);
        }

        return encoded;
    }

    private static void KillFfmpeg(FFMpegVideoWriter writer)
    {
        var process = (Process)typeof(FFMpegVideoWriter)
            .GetField("_ffmpeg", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(writer)!;
        process.Kill(entireProcessTree: true);
        process.WaitForExit();
    }

    private sealed class CountingFrameCapture : IRawFrameCapture
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Capture(byte[] frameData)
        {
            var index = Interlocked.Increment(ref _count);
            Array.Fill(frameData, (byte)(index * 7));
        }

        public void Dispose()
        {
        }
    }
}

internal static class RealRecordingPrerequisites
{
    private static readonly Lazy<string?> Ffmpeg = new(ProbeFfmpeg);
    private static readonly Lazy<(string? Device, string? SkipReason)> Probe = new(ProbeMicrophone);

    public static string? FfmpegSkipReason => Ffmpeg.Value;

    public static string? MicrophoneSkipReason => Ffmpeg.Value ?? Probe.Value.SkipReason;

    public static string Device => Probe.Value.Device!;

    private static string? ProbeFfmpeg()
    {
        try
        {
            Run("-version");
            return null;
        }
        catch (Exception exception)
        {
            return $"ffmpeg is not installed or configured: {exception.Message}";
        }
    }

    private static (string? Device, string? SkipReason) ProbeMicrophone()
    {
        try
        {
            var list = Run("-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy");
            var match = Regex.Match(list, "\"(?<name>[^\"]+)\" \\(audio\\)");
            if (!match.Success)
            {
                return (null, "No dshow audio capture device was found.");
            }

            var device = match.Groups["name"].Value;
            var capture = Run("-hide_banner", "-f", "dshow", "-i", $"audio={device}", "-t", "1", "-f", "null", "-");
            return capture.Contains("Error opening input", StringComparison.OrdinalIgnoreCase) || capture.Contains("Could not run graph", StringComparison.OrdinalIgnoreCase)
                ? (null, $"The microphone '{device}' could not be opened (check Windows microphone privacy settings): {capture.Trim().Split('\n').Last().Trim()}")
                : (device, null);
        }
        catch (Exception exception)
        {
            return (null, $"Microphone discovery failed: {exception.Message}");
        }
    }

    private static string Run(params string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = FfmpegResolver.Resolve(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = Process.Start(psi)!;
        _ = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15_000))
        {
            process.Kill(entireProcessTree: true);
        }

        return stderr.GetAwaiter().GetResult();
    }
}
