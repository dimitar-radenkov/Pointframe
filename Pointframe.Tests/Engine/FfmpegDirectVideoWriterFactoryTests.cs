using System.IO;
using Pointframe.Engine;
using Xunit;

namespace Pointframe.Tests.Engine;

[Collection("DirectFfmpegPathOverride")]
public sealed class FfmpegDirectVideoWriterFactoryTests
{
    [Fact]
    public void GetAvailability_WithConfiguredPathToExistingFile_ReportsFoundFromEnvironmentVariable()
    {
        var configuredPath = Path.Combine(Path.GetTempPath(), $"pointframe-ffmpeg-test-{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(configuredPath, []);
        var original = Environment.GetEnvironmentVariable("POINTFRAME_FFMPEG_PATH");
        try
        {
            Environment.SetEnvironmentVariable("POINTFRAME_FFMPEG_PATH", configuredPath);

            var availability = FfmpegDirectVideoWriterFactory.GetAvailability();

            Assert.True(availability.Found);
            Assert.Equal(configuredPath, availability.Path);
            Assert.Equal("EnvironmentVariable", availability.Source);
        }
        finally
        {
            Environment.SetEnvironmentVariable("POINTFRAME_FFMPEG_PATH", original);
            File.Delete(configuredPath);
        }
    }

    [Fact]
    public void GetAvailability_WithConfiguredPathToMissingFile_ReportsNotFoundFromEnvironmentVariable()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"pointframe-ffmpeg-missing-{Guid.NewGuid():N}.exe");
        var original = Environment.GetEnvironmentVariable("POINTFRAME_FFMPEG_PATH");
        try
        {
            Environment.SetEnvironmentVariable("POINTFRAME_FFMPEG_PATH", missingPath);

            var availability = FfmpegDirectVideoWriterFactory.GetAvailability();

            Assert.False(availability.Found);
            Assert.Equal(missingPath, availability.Path);
            Assert.Equal("EnvironmentVariable", availability.Source);
        }
        finally
        {
            Environment.SetEnvironmentVariable("POINTFRAME_FFMPEG_PATH", original);
        }
    }

    [Fact]
    public void GetAvailability_WithoutConfiguredPath_ReturnsNonEmptyPathAndKnownSource()
    {
        var original = Environment.GetEnvironmentVariable("POINTFRAME_FFMPEG_PATH");
        try
        {
            Environment.SetEnvironmentVariable("POINTFRAME_FFMPEG_PATH", null);

            var availability = FfmpegDirectVideoWriterFactory.GetAvailability();

            Assert.False(string.IsNullOrWhiteSpace(availability.Path));
            Assert.Contains(availability.Source, new[] { "Bundled", "Path", "NotFound" });
        }
        finally
        {
            Environment.SetEnvironmentVariable("POINTFRAME_FFMPEG_PATH", original);
        }
    }
}
