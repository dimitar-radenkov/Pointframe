using System.Text.Json;
using Pointframe.Engine;
using Pointframe.Mcp;
using Xunit;

namespace Pointframe.Tests.Mcp;

public sealed class DirectRecordingMcpServiceTests
{
    [Fact]
    public void StartRecording_MapsEngineStateErrorToStructuredJson()
    {
        var engine = new FakeDirectRecordingService
        {
            StartResult = new DirectRecordingResult(false, ErrorCode: "recording_already_active", ErrorMessage: "A recording is active."),
        };
        var sut = new DirectRecordingMcpService(engine);

        var json = sut.StartRecording(@"\\.\DISPLAY1", [], 20);
        var response = JsonSerializer.Deserialize<DirectRecordingResponse>(json);

        Assert.NotNull(response);
        Assert.False(response.Success);
        Assert.Equal("recording_already_active", response.Error?.Code);
        Assert.Equal(@"\\.\DISPLAY1", engine.Request?.MonitorName);
        Assert.Empty(engine.Request?.RedactionRegionsCaptureLocalPixels ?? []);
    }

    [Fact]
    public async Task StopRecordingAsync_MapsFinalizedEngineArtifactToStructuredJson()
    {
        var artifact = new DirectRecordingArtifact(1, "rec_1", "video/mp4", "C:\\recording.mp4", "ABC", 3, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), false, @"\\.\DISPLAY1", 1d, 1d, new Pointframe.Engine.PixelBounds(0, 0, 2, 2), new Pointframe.Engine.PixelBounds(0, 0, 2, 2), new Pointframe.Engine.PixelBounds(0, 0, 2, 2), "C:\\recording.mp4.events.jsonl", 2, 1);
        var sut = new DirectRecordingMcpService(new FakeDirectRecordingService
        {
            StopResult = new DirectRecordingResult(true, Artifact: artifact),
        });

        var json = await sut.StopRecordingAsync();
        var response = JsonSerializer.Deserialize<DirectRecordingResponse>(json);

        Assert.NotNull(response);
        Assert.True(response.Success);
        Assert.Equal(artifact, response.Artifact);
    }

    private sealed class FakeDirectRecordingService : IDirectRecordingService
    {
        public DirectRecordingRequest? Request { get; private set; }
        public DirectRecordingResult StartResult { get; set; } = new(true);
        public DirectRecordingResult StopResult { get; set; } = new(true);

        public DirectRecordingResult Start(DirectRecordingRequest request)
        {
            Request = request;
            return StartResult;
        }

        public Task<DirectRecordingResult> StopAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(StopResult);
        }

        public void Dispose()
        {
        }
    }
}