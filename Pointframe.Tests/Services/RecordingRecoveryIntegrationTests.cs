using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

[Collection("FfmpegPathOverride")]
public sealed class RecordingRecoveryIntegrationTests : IDisposable
{
    private const int Width = 64;
    private const int Height = 64;
    private const int Fps = 30;

    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [FfmpegFact]
    [Trait("Category", "Integration")]
    public void CleanStop_ProducesFaststartH264Mp4AndRemovesIntermediate()
    {
        var output = _folder.File("clean.mp4");
        var writer = new FFMpegVideoWriter(Width, Height, Fps, output, NullLogger.Instance);
        WriteSeconds(writer, 5);
        writer.Dispose();

        var recovered = CreateRecovery().Recover(writer.IntermediatePath, output);

        Assert.NotNull(recovered);
        Assert.True(recovered.Remuxed);
        Assert.Equal(output, recovered.FilePath);
        Assert.InRange(recovered.Duration.TotalSeconds, 4.7, 5.3);
        Assert.Equal([output], Directory.GetFileSystemEntries(_folder.Path));
        AssertFaststart(output);
        var info = Ffmpeg("-hide_banner", "-i", output).Stderr;
        Assert.Contains("Video: h264", info);
        Assert.Contains("yuv420p", info);
        AssertSeekable(output, seekSeconds: 3);
    }

    [FfmpegFact]
    [Trait("Category", "Integration")]
    public void KilledAfterFiveSecondsOfVideo_RecoversSeekablePartialRecording()
    {
        var output = _folder.File("killed.mp4");
        var writer = new FFMpegVideoWriter(Width, Height, Fps, output, NullLogger.Instance);
        WriteSeconds(writer, 5);
        KillAndDispose(writer);

        var recovered = CreateRecovery().Recover(writer.IntermediatePath, output);

        Assert.NotNull(recovered);
        Assert.True(recovered.Remuxed);
        Assert.InRange(recovered.Duration.TotalSeconds, 5 - 1.5, 5.1);
        Assert.Equal([output], Directory.GetFileSystemEntries(_folder.Path));
        AssertFaststart(output);
        AssertSeekable(output, seekSeconds: 2);
    }

    [FfmpegFact]
    [Trait("Category", "Integration")]
    public void KilledWithAudio_RecoversTheAudioStream()
    {
        var output = _folder.File("audio.mp4");
        var intermediate = RecordingRecovery.GetIntermediatePath(output);
        var psi = new ProcessStartInfo
        {
            FileName = FfmpegResolver.Resolve(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[]
        {
            "-y", "-re",
            "-f", "lavfi", "-i", $"testsrc=size={Width}x{Height}:rate={Fps}",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-g", $"{Fps}",
            "-c:a", "aac", "-b:a", "128k", "-shortest",
            "-movflags", "+frag_keyframe+empty_moov+default_base_moof", "-flush_packets", "1",
            intermediate,
        })
        {
            psi.ArgumentList.Add(argument);
        }

        using (var process = Process.Start(psi)!)
        {
            _ = process.StandardError.ReadToEndAsync();
            Thread.Sleep(TimeSpan.FromSeconds(5));
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }

        var recovered = CreateRecovery().Recover(intermediate, output);

        Assert.NotNull(recovered);
        Assert.True(recovered.Remuxed);
        Assert.InRange(recovered.Duration.TotalSeconds, 2.5, 5.5);
        Assert.Contains("Audio: aac", Ffmpeg("-hide_banner", "-i", output).Stderr);
        AssertFaststart(output);
    }

    [FfmpegFact]
    [Trait("Category", "Integration")]
    public void KilledBeforeFirstFragment_LeavesNothingBehind()
    {
        var output = _folder.File("early.mp4");
        var writer = new FFMpegVideoWriter(Width, Height, Fps, output, NullLogger.Instance);
        WriteFrames(writer, 10);
        Thread.Sleep(TimeSpan.FromSeconds(1));
        KillAndDispose(writer);

        var recovered = CreateRecovery().Recover(writer.IntermediatePath, output);

        Assert.Null(recovered);
        Assert.Empty(Directory.GetFileSystemEntries(_folder.Path));
    }

    [FfmpegFact]
    [Trait("Category", "Integration")]
    public void WhenRemuxFails_KeepsAPlayableRecoveredFile()
    {
        var output = _folder.File("failed-remux.mp4");
        var writer = new FFMpegVideoWriter(Width, Height, Fps, output, NullLogger.Instance);
        WriteSeconds(writer, 5);
        KillAndDispose(writer);

        var recovered = new RecordingRecovery(new FakeRemuxer { RemuxResult = null }, NullLogger<RecordingRecovery>.Instance)
            .Recover(writer.IntermediatePath, output);

        Assert.NotNull(recovered);
        Assert.False(recovered.Remuxed);
        Assert.Equal(_folder.File("failed-remux.recovered.mp4"), recovered.FilePath);
        Assert.False(File.Exists(output));
        Assert.False(File.Exists(writer.IntermediatePath));
        var decode = Ffmpeg("-v", "error", "-i", recovered.FilePath, "-f", "null", "-");
        Assert.Equal(0, decode.ExitCode);
        Assert.Equal(string.Empty, decode.Stderr.Trim());
    }

    [FfmpegFact]
    [Trait("Category", "Integration")]
    public void LeftoverFromAnEarlierSession_IsRecoveredOnceOnStart()
    {
        var output = _folder.File("leftover.mp4");
        var writer = new FFMpegVideoWriter(Width, Height, Fps, output, NullLogger.Instance);
        WriteSeconds(writer, 5);
        KillAndDispose(writer);
        File.SetLastWriteTimeUtc(writer.IntermediatePath, DateTime.UtcNow.AddHours(-1));
        var recovery = CreateRecovery();

        var first = recovery.RecoverLeftovers(_folder.Path, DateTime.UtcNow);
        var writtenAt = File.GetLastWriteTimeUtc(output);
        var second = recovery.RecoverLeftovers(_folder.Path, DateTime.UtcNow);

        Assert.Equal(1, first);
        Assert.Equal(0, second);
        Assert.Equal([output], Directory.GetFileSystemEntries(_folder.Path));
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(output));
        AssertSeekable(output, seekSeconds: 2);
    }

    private static RecordingRecovery CreateRecovery() =>
        new(new FfmpegRecordingRemuxer(NullLogger<FfmpegRecordingRemuxer>.Instance), NullLogger<RecordingRecovery>.Instance);

    private static void WriteSeconds(FFMpegVideoWriter writer, int seconds) => WriteFrames(writer, seconds * Fps);

    private static void WriteFrames(FFMpegVideoWriter writer, int frameCount)
    {
        var frame = new byte[Width * Height * 4];
        for (var index = 0; index < frameCount; index++)
        {
            Array.Fill(frame, (byte)(index * 7));
            writer.WriteFrame(frame);
        }
    }

    private static void KillAndDispose(FFMpegVideoWriter writer)
    {
        Thread.Sleep(TimeSpan.FromSeconds(2));
        var process = (Process)typeof(FFMpegVideoWriter)
            .GetField("_ffmpeg", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(writer)!;
        process.Kill(entireProcessTree: true);
        process.WaitForExit();
        Assert.ThrowsAny<Exception>(writer.Dispose);
    }

    private static void AssertFaststart(string path)
    {
        var boxes = ReadTopLevelBoxes(path);
        Assert.Contains("moov", boxes);
        Assert.Contains("mdat", boxes);
        Assert.True(boxes.IndexOf("moov") < boxes.IndexOf("mdat"), $"moov must precede mdat, found {string.Join(",", boxes)}");
        Assert.DoesNotContain("moof", boxes);
    }

    private static void AssertSeekable(string path, int seekSeconds)
    {
        var result = Ffmpeg("-v", "error", "-ss", seekSeconds.ToString(), "-i", path, "-frames:v", "1", "-f", "null", "-");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Stderr.Trim());
    }

    private static List<string> ReadTopLevelBoxes(string path)
    {
        var boxes = new List<string>();
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[8];
        while (stream.Position + 8 <= stream.Length)
        {
            var start = stream.Position;
            stream.ReadExactly(header);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            boxes.Add(Encoding.ASCII.GetString(header[4..]));
            if (size < 8)
            {
                break;
            }

            stream.Position = start + size;
        }

        return boxes;
    }

    private static (int ExitCode, string Stderr) Ffmpeg(params string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = FfmpegResolver.Resolve(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(30_000), "ffmpeg did not finish.");
        return (process.ExitCode, stderr.GetAwaiter().GetResult());
    }
}
