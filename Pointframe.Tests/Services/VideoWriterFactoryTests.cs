using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

[Collection("FfmpegPathOverride")]
public sealed class VideoWriterFactoryTests
{
    private static VideoWriterFactory CreateSut()
    {
        var settings = new Mock<IUserSettingsService>();
        settings.SetupGet(service => service.Current).Returns(new UserSettings());
        return new(NullLogger<FFMpegVideoWriter>.Instance, settings.Object);
    }

    private static IDisposable UseFfmpegPathOverride(string? path)
    {
        Monitor.Enter(typeof(VideoWriterFactoryTests));
        var previous = AppContext.GetData("SnippingTool.FfmpegPath");
        AppContext.SetData("SnippingTool.FfmpegPath", path);
        return new ActionDisposable(() =>
        {
            AppContext.SetData("SnippingTool.FfmpegPath", previous);
            Monitor.Exit(typeof(VideoWriterFactoryTests));
        });
    }

    [Fact]
    public void Create_ReturnsFfmpegVideoWriter_WhenFfmpegExists()
    {
        var fakeFfmpeg = Path.Combine(Path.GetTempPath(), $"ffmpeg-{Guid.NewGuid()}.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), fakeFfmpeg, overwrite: true);

        using var _ = UseFfmpegPathOverride(fakeFfmpeg);
        var factory = CreateSut();
        var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"test-{Guid.NewGuid()}.mp4");

        try
        {
            using var writer = factory.Create(100, 100, 10, tempPath, null);
            Assert.IsType<FFMpegVideoWriter>(writer);
        }
        finally
        {
            // The writer may still be releasing the fake ffmpeg process when Dispose returns (#322).
            DeleteWithRetry(fakeFfmpeg);
            DeleteWithRetry(tempPath);
        }
    }

    [Fact]
    public void Create_Mp4_ThrowsFileNotFoundWhenFfmpegMissing()
    {
        using var _ = UseFfmpegPathOverride(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid()}.exe"));

        var factory = CreateSut();

        Assert.Throws<System.IO.FileNotFoundException>(() =>
            factory.Create(100, 100, 10, "test.mp4", null));
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup: after the last attempt a leftover temp file must not replace a pending assertion failure.
                Thread.Sleep(100);
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
