using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

[Collection("FfmpegPathOverride")]
public sealed class GifExportServiceTests
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
    public async Task ExportAsync_ThrowsFileNotFoundException_WhenFfmpegMissing()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid()}.exe");
        using var overrideScope = UseFfmpegPathOverride(missingPath);

        var svc = new GifExportService(NullLogger<GifExportService>.Instance);

        var ex = await Assert.ThrowsAsync<FileNotFoundException>(
            () => svc.Export(@"C:\input.mp4", @"C:\output.gif", fps: 10));

        Assert.Equal(missingPath, ex.FileName);
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
