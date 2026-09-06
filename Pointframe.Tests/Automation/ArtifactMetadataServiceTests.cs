using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Pointframe.Automation.Bridge;
using Xunit;

namespace Pointframe.Tests.Automation;

public sealed class ArtifactMetadataServiceTests
{
    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    [Fact]
    public async Task WriteImageMetadataAsync_WhenArtifactExists_WritesMatchingSidecar()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pointframe-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var artifactPath = Path.Combine(directory, "capture.png");
        byte[] artifactBytes = [1, 2, 3, 4];
        await File.WriteAllBytesAsync(artifactPath, artifactBytes);
        var sut = new ArtifactMetadataService(TimeProvider.System);

        try
        {
            var result = await sut.WriteImageMetadataAsync(new ImageArtifactMetadataRequest(
                artifactPath,
                "agent",
                @"\\.\DISPLAY1",
                1.5d,
                1.5d,
                new PixelBounds(0, 0, 2560, 1440)));

            var metadataPath = $"{artifactPath}.metadata.json";
            var persisted = await File.ReadAllTextAsync(metadataPath);
            var deserialized = JsonSerializer.Deserialize<ImageArtifactMetadata>(persisted);

            Assert.Equal(1, result.SchemaVersion);
            Assert.Equal("image/png", result.Kind);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(artifactBytes)), result.Sha256);
            Assert.Equal(artifactBytes.Length, result.ByteLength);
            Assert.True(File.Exists(metadataPath));
            Assert.Equal(result, deserialized);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task WriteImageMetadataAsync_WhenArtifactDoesNotExist_ThrowsFileNotFoundException()
    {
        var sut = new ArtifactMetadataService(TimeProvider.System);

        await Assert.ThrowsAsync<FileNotFoundException>(() => sut.WriteImageMetadataAsync(new ImageArtifactMetadataRequest(
            Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.png"),
            "agent",
            @"\\.\DISPLAY1",
            1d,
            1d,
            new PixelBounds(0, 0, 1, 1))));
    }

    [Fact]
    public async Task WriteRecordingMetadataAsync_WhenArtifactExists_WritesFinalizedRecordingSidecar()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pointframe-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var artifactPath = Path.Combine(directory, "recording.mp4");
        byte[] artifactBytes = [5, 6, 7, 8];
        await File.WriteAllBytesAsync(artifactPath, artifactBytes);
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero));
        var sut = new ArtifactMetadataService(timeProvider);
        var eventTrackSummary = new RecordingEventTrackSummary(
            $"{artifactPath}.events.jsonl",
            EventCount: 5,
            SchemaVersion: 1);
        var geometry = new RecordingSessionGeometry(
            new System.Windows.Int32Rect(-1920, 0, 1920, 1080),
            new System.Windows.Int32Rect(-1800, 20, 1280, 720),
            new System.Windows.Int32Rect(-1920, 0, 1920, 1040),
            new System.Windows.Rect(0, 0, 1920, 1080),
            new System.Windows.Rect(0, 0, 1920, 1040),
            new System.Windows.Rect(120, 20, 1280, 720),
            @"\\.\DISPLAY2",
            1.25d,
            1.25d);

        try
        {
            var result = await sut.WriteRecordingMetadataAsync(new RecordingArtifactMetadataRequest(
                artifactPath,
                TimeSpan.FromSeconds(83),
                HadMicrophoneAudio: true,
                geometry,
                eventTrackSummary));

            var persisted = await File.ReadAllTextAsync($"{artifactPath}.metadata.json");
            var deserialized = JsonSerializer.Deserialize<RecordingArtifactMetadata>(persisted);

            Assert.Equal(1, result.SchemaVersion);
            Assert.StartsWith("rec_", result.ArtifactId, StringComparison.Ordinal);
            Assert.Equal("video/mp4", result.Kind);
            Assert.Equal(Path.GetFullPath(artifactPath), result.Path);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(artifactBytes)), result.Sha256);
            Assert.Equal(artifactBytes.Length, result.ByteLength);
            Assert.Equal(timeProvider.GetUtcNow(), result.CreatedUtc);
            Assert.Equal(TimeSpan.FromSeconds(83), result.ElapsedDuration);
            Assert.True(result.HadMicrophoneAudio);
            Assert.Equal(@"\\.\DISPLAY2", result.MonitorName);
            Assert.Equal(new PixelBounds(-1800, 20, 1280, 720), result.CaptureBoundsPixels);
            Assert.Equal(new PixelBounds(-1920, 0, 1920, 1080), result.HostBoundsPixels);
            Assert.Equal(new PixelBounds(-1920, 0, 1920, 1040), result.WorkAreaBoundsPixels);
            Assert.Equal(eventTrackSummary.SidecarPath, result.EventSidecarPath);
            Assert.Equal(eventTrackSummary.EventCount, result.EventCount);
            Assert.Equal(eventTrackSummary.SchemaVersion, result.EventTrackSchemaVersion);
            Assert.Equal(result, deserialized);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}