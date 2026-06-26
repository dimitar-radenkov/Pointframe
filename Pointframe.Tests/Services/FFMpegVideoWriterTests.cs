using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Pointframe.Models;
using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

[Collection("FfmpegPathOverride")]
public sealed class FFMpegVideoWriterTests
{
    private static readonly object FfmpegPathLock = new();

    private static IDisposable UseFfmpegPathOverride(string? path)
    {
        Monitor.Enter(FfmpegPathLock);
        var previous = AppContext.GetData("SnippingTool.FfmpegPath");
        AppContext.SetData("SnippingTool.FfmpegPath", path);
        return new ActionDisposable(() =>
        {
            AppContext.SetData("SnippingTool.FfmpegPath", previous);
            Monitor.Exit(FfmpegPathLock);
        });
    }

    [Fact]
    public void ResolveFfmpegPath_PrefersAppDirectoryFile()
    {
        using var overrideScope = UseFfmpegPathOverride(null);

        var appFfmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        var assetsFfmpeg = Path.Combine(AppContext.BaseDirectory, "Assets", "ffmpeg", "ffmpeg.exe");
        using var fileScope = new FfmpegFileScope(appFfmpeg, assetsFfmpeg);

        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), appFfmpeg, overwrite: true);

        var resolved = InvokeResolveFfmpegPath();

        Assert.Equal(appFfmpeg, resolved);
    }

    [Fact]
    public void ResolveFfmpegPath_PrefersAssetsFolderWhenAppFileMissing()
    {
        using var overrideScope = UseFfmpegPathOverride(null);

        var appFfmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        var assetsFfmpeg = Path.Combine(AppContext.BaseDirectory, "Assets", "ffmpeg", "ffmpeg.exe");
        using var fileScope = new FfmpegFileScope(appFfmpeg, assetsFfmpeg);

        Directory.CreateDirectory(Path.GetDirectoryName(assetsFfmpeg)!);
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), assetsFfmpeg, overwrite: true);

        var resolved = InvokeResolveFfmpegPath();

        Assert.Equal(assetsFfmpeg, resolved);
    }

    [Fact]
    public void ResolveFfmpegPath_FallsBackToExecutableName()
    {
        using var overrideScope = UseFfmpegPathOverride(null);

        using var fileScope = new FfmpegFileScope(
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "Assets", "ffmpeg", "ffmpeg.exe"));

        var resolved = InvokeResolveFfmpegPath();

        Assert.Equal("ffmpeg.exe", resolved);
    }

    [Fact]
    public void Dispose_IsIdempotentAndClosesStream()
    {
        var writer = CreateDetachedWriter(exitCode: 0, out var memoryStream);

        writer.WriteFrame([1, 2, 3, 4]);
        Assert.Equal(4, memoryStream.Length);

        writer.Dispose();
        writer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => memoryStream.WriteByte(5));
    }

    [Fact]
    public void Dispose_HandlesNonZeroExitCode()
    {
        var writer = CreateDetachedWriter(exitCode: 1, out _);

        writer.Dispose();
    }

    [Fact]
    public void Constructor_ThrowsWhenFfmpegExeIsMissing()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid()}.exe");
        using var overrideScope = UseFfmpegPathOverride(missingPath);

        var exception = Assert.Throws<FileNotFoundException>(() => new FFMpegVideoWriter(1, 1, 30, Path.GetTempFileName(), NullLogger.Instance));

        Assert.Equal(missingPath, exception.FileName);
    }

    [Fact]
    public void Constructor_WithFakeFfmpegExe_StartsAndDisposes()
    {
        var fakeFfmpeg = Path.Combine(Path.GetTempPath(), $"ffmpeg-{Guid.NewGuid()}.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), fakeFfmpeg, overwrite: true);

        using var overrideScope = UseFfmpegPathOverride(fakeFfmpeg);

        try
        {
            using var writer = new FFMpegVideoWriter(2, 2, 30, Path.GetTempFileName(), NullLogger.Instance);
            writer.Dispose();
        }
        finally
        {
            File.Delete(fakeFfmpeg);
        }
    }

    [Fact]
    public void BuildArguments_WithoutMicrophone_DoesNotAddAudioInput()
    {
        var args = new List<string>();
        FFMpegVideoWriter.BuildArguments(args, 128, 72, 20, "capture.mp4", null);

        Assert.DoesNotContain("-use_wallclock_as_timestamps", args);
        Assert.DoesNotContain("dshow", args);
        Assert.DoesNotContain("-map", args);
        Assert.DoesNotContain("aac", args);
        Assert.DoesNotContain("-shortest", args);
    }

    [Fact]
    public void BuildArguments_SetsOneSecondKeyframeInterval()
    {
        var args = new List<string>();
        FFMpegVideoWriter.BuildArguments(args, 128, 72, 20, "capture.mp4", null);

        var gopIndex = args.IndexOf("-g");
        Assert.True(gopIndex >= 0);
        Assert.Equal("20", args[gopIndex + 1]);
    }

    [Fact]
    public void BuildArguments_WithMicrophone_AddsDirectShowAudioInput()
    {
        var args = new List<string>();
        FFMpegVideoWriter.BuildArguments(args, 128, 72, 20, "capture.mp4", "Studio Mic");

        Assert.Contains("-use_wallclock_as_timestamps", args);
        Assert.Contains("dshow", args);
        Assert.Contains("audio=Studio Mic", args);
        Assert.Contains("0:v:0", args);
        Assert.Contains("1:a:0", args);
        Assert.Contains("aac", args);
        Assert.Contains("-shortest", args);
    }

    [Fact]
    public void BuildArguments_WithMicrophone_AddsWallclockTimestampOptionBeforeRawVideoInput()
    {
        var args = new List<string>();
        FFMpegVideoWriter.BuildArguments(args, 128, 72, 20, "capture.mp4", "Studio Mic");

        var wallclockIndex = args.IndexOf("-use_wallclock_as_timestamps");
        var pipeInputIndex = args.IndexOf("pipe:0");

        Assert.True(wallclockIndex >= 0);
        Assert.True(pipeInputIndex > wallclockIndex);
    }

    [Fact]
    public void BuildArguments_WithQuotedMicrophone_PassesDeviceNameUnescaped()
    {
        var args = new List<string>();
        FFMpegVideoWriter.BuildArguments(args, 128, 72, 20, "capture.mp4", "Studio \"Mic\"");

        Assert.Contains("audio=Studio \"Mic\"", args);
    }

    [Fact]
    public void BuildArguments_WhenDrawtextFilterProvided_AddsVideoFilter()
    {
        var args = new List<string>();

        FFMpegVideoWriter.BuildArguments(args, 128, 72, 20, "capture.mp4", null, "drawtext=fontfile='C\\:/Windows/Fonts/segoeui.ttf':text='stamp'");

        var vfIndex = args.IndexOf("-vf");
        Assert.True(vfIndex >= 0);
        Assert.Contains("drawtext=", args[vfIndex + 1]);
    }

    [Fact]
    public void BuildArguments_WhenDrawtextFilterMissing_DoesNotAddVideoFilter()
    {
        var args = new List<string>();

        FFMpegVideoWriter.BuildArguments(args, 128, 72, 20, "capture.mp4", null, null);

        Assert.DoesNotContain("-vf", args);
    }

    [Fact]
    public void BuildDrawtextFilter_EscapesColonPercentAndQuote()
    {
        var settings = new ScreenshotWatermarkSettings
        {
            Enabled = true,
            TextTemplate = WatermarkTextTemplate.DateTime,
            Position = WatermarkPosition.BottomRight,
            FontSize = 18,
            ColorHex = "#FFFFFFFF",
            BackgroundEnabled = true,
            Opacity = 1.0,
            Margin = 16,
        };

        var escaped = FFMpegVideoWriter.EscapeDrawtextValue("A:B % C ' D");
        Assert.Equal("A\\:B \\% C \\' D", escaped);

        var filter = FFMpegVideoWriter.BuildDrawtextFilter(
            settings,
            new DateTimeOffset(2026, 3, 18, 14, 5, 9, TimeSpan.FromHours(2)),
            "Pointframe",
            @"C:\Windows\Fonts\segoeui.ttf");

        Assert.Contains("drawtext=", filter);
        Assert.Contains("fontfile='C\\:/Windows/Fonts/segoeui.ttf'", filter);
        Assert.Contains(":x=w-tw-16", filter);
        Assert.Contains(":y=h-th-16", filter);
    }

    private static string InvokeResolveFfmpegPath()
    {
        return FfmpegResolver.Resolve();
    }

    private static FFMpegVideoWriter CreateDetachedWriter(int exitCode, out MemoryStream memoryStream)
    {
        var writer = (FFMpegVideoWriter)RuntimeHelpers.GetUninitializedObject(typeof(FFMpegVideoWriter));
        memoryStream = new MemoryStream();
        SetField(writer, "_stdin", memoryStream);
        SetField(writer, "_logger", NullLogger.Instance);

        var process = new Process
        {
            StartInfo = new ProcessStartInfo("cmd.exe", $"/c exit {exitCode}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = false,
                RedirectStandardError = false,
            }
        };

        process.Start();
        process.WaitForExit(TimeSpan.FromSeconds(5));
        SetField(writer, "_ffmpeg", process);

        return writer;
    }

    private static void SetField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(target, value);
    }

    private sealed class FfmpegFileScope : IDisposable
    {
        private readonly string _appFfmpegPath;
        private readonly string _assetsFfmpegPath;
        private readonly string? _appBackupPath;
        private readonly string? _assetsBackupPath;

        public FfmpegFileScope(string appFfmpegPath, string assetsFfmpegPath)
        {
            _appFfmpegPath = appFfmpegPath;
            _assetsFfmpegPath = assetsFfmpegPath;
            _appBackupPath = BackupIfExists(_appFfmpegPath);
            _assetsBackupPath = BackupIfExists(_assetsFfmpegPath);
        }

        public void Dispose()
        {
            Restore(_appFfmpegPath, _appBackupPath);
            Restore(_assetsFfmpegPath, _assetsBackupPath);
        }

        private static string? BackupIfExists(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var backup = path + ".bak." + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Move(path, backup);
            return backup;
        }

        private static void Restore(string path, string? backup)
        {
            DeleteWithRetry(path);

            if (backup is not null)
            {
                MoveWithRetry(backup, path);
            }
        }

        private static void DeleteWithRetry(string path)
        {
            for (var attempt = 0; attempt < 50; attempt++)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }

                    return;
                }
                catch (Exception ex) when (attempt < 49 && ex is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(100);
                }
            }
        }

        private static void MoveWithRetry(string sourcePath, string destinationPath)
        {
            for (var attempt = 0; attempt < 50; attempt++)
            {
                try
                {
                    File.Move(sourcePath, destinationPath);
                    return;
                }
                catch (Exception ex) when (attempt < 49 && ex is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(100);
                }
            }
        }
    }

    private sealed class ActionDisposable : IDisposable
    {
        private readonly Action _dispose;

        public ActionDisposable(Action dispose)
        {
            _dispose = dispose;
        }

        public void Dispose() => _dispose();
    }
}
