using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Pointframe.Engine;
using Xunit;

namespace Pointframe.Tests.Engine;

public sealed class DirectCaptureServiceTests : IDisposable
{
    private readonly string _screenshotsDirectory = Path.Combine(Path.GetTempPath(), $"Pointframe.Tests.{Guid.NewGuid():N}");

    [Fact]
    public async Task CaptureMonitorAsync_WritesPngAndReturnsVerifiedArtifactMetadata()
    {
        var display = new Pointframe.Engine.DisplayDescriptor(
            @"\\.\DISPLAY1",
            1.5,
            1.25,
            new Pointframe.Engine.PixelBounds(-20, 10, 2, 3));
        var sut = new DirectCaptureService(new FakeDisplayCaptureEngine(display), _screenshotsDirectory);

        var json = await sut.CaptureMonitorAsync(display.MonitorName);
        var response = JsonSerializer.Deserialize<DirectCaptureResponse>(json);

        Assert.NotNull(response);
        var artifact = Assert.IsType<ArtifactDescriptor>(response.Artifact);
        var metadata = artifact.Metadata;
        Assert.True(response.Success);
        Assert.Equal("image/png", metadata.Kind);
        Assert.True(File.Exists(metadata.Path));
        var metadataSidecarPath = $"{metadata.Path}.metadata.json";
        Assert.True(File.Exists(metadataSidecarPath));
        Assert.Equal(new FileInfo(metadata.Path).Length, metadata.ByteLength);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(metadata.Path))), metadata.Sha256);
        var persistedMetadata = JsonSerializer.Deserialize<ImageArtifactMetadata>(File.ReadAllText(metadataSidecarPath));
        Assert.Equal(metadata, persistedMetadata);
        Assert.Equal(display.MonitorName, metadata.MonitorName);
        Assert.Equal(display.DpiScaleX, metadata.DpiScaleX);
        Assert.Equal(display.DpiScaleY, metadata.DpiScaleY);
        Assert.Equal(new Pointframe.Engine.PixelBounds(-20, 10, 2, 3), metadata.CaptureBoundsPixels);
    }

    [Fact]
    public void ListDisplays_ReturnsCurrentCompatibleDisplayMetadata()
    {
        var display = new Pointframe.Engine.DisplayDescriptor(
            @"\\.\DISPLAY1",
            1d,
            1d,
            new Pointframe.Engine.PixelBounds(0, 0, 100, 200));
        var sut = new DirectCaptureService(new FakeDisplayCaptureEngine(display), _screenshotsDirectory);

        var response = JsonSerializer.Deserialize<DirectCaptureResponse>(sut.ListDisplays());

        Assert.NotNull(response);
        Assert.True(response.Success);
        var returnedDisplay = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<Pointframe.Engine.DisplayDescriptor>>(response.Displays));
        Assert.Equal(display.MonitorName, returnedDisplay.MonitorName);
        Assert.Equal(new Pointframe.Engine.PixelBounds(0, 0, 100, 200), returnedDisplay.BoundsPixels);
    }

    public void Dispose()
    {
        if (Directory.Exists(_screenshotsDirectory))
        {
            Directory.Delete(_screenshotsDirectory, recursive: true);
        }
    }

    private sealed class FakeDisplayCaptureEngine(Pointframe.Engine.DisplayDescriptor display) : IDisplayCaptureEngine
    {
        public IReadOnlyList<Pointframe.Engine.DisplayDescriptor> GetDisplays()
        {
            return [display];
        }

        public Bitmap Capture(Pointframe.Engine.PixelBounds boundsPixels)
        {
            return new Bitmap(boundsPixels.Width, boundsPixels.Height);
        }

        public CapturedMonitor CaptureMonitor(string monitorName)
        {
            if (!string.Equals(display.MonitorName, monitorName, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Unknown monitor.", nameof(monitorName));
            }

            return new CapturedMonitor(display, Capture(display.BoundsPixels));
        }
    }
}