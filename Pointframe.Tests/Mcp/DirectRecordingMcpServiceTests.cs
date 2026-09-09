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

    [Fact]
    public void GetRecordingStatus_WhenNoRecordingIsActive_ReturnsNotRecordingWithoutSession()
    {
        var sut = new DirectRecordingMcpService(new FakeDirectRecordingService
        {
            StatusResult = new DirectRecordingStatus(1, false),
        });

        var json = sut.GetRecordingStatus();
        var status = JsonSerializer.Deserialize<DirectRecordingStatus>(json);

        Assert.NotNull(status);
        Assert.False(status.IsRecording);
        Assert.Null(status.Session);
        Assert.Null(status.Elapsed);
    }

    [Fact]
    public void GetRecordingStatus_WhenRecordingIsActive_ReturnsSessionAndElapsed()
    {
        var session = new DirectRecordingSession(1, "rec_1", "C:\\recording.mp4", @"\\.\DISPLAY1", 20, new Pointframe.Engine.PixelBounds(0, 0, 2, 2), [], DateTimeOffset.UtcNow);
        var sut = new DirectRecordingMcpService(new FakeDirectRecordingService
        {
            StatusResult = new DirectRecordingStatus(1, true, session, TimeSpan.FromSeconds(5)),
        });

        var json = sut.GetRecordingStatus();
        var status = JsonSerializer.Deserialize<DirectRecordingStatus>(json);

        Assert.NotNull(status);
        Assert.True(status.IsRecording);
        Assert.Equal(session.OperationId, status.Session?.OperationId);
        Assert.Equal(session.MonitorName, status.Session?.MonitorName);
        Assert.Equal(session.ArtifactPath, status.Session?.ArtifactPath);
        Assert.Equal(session.StartedUtc, status.Session?.StartedUtc);
        Assert.Equal(TimeSpan.FromSeconds(5), status.Elapsed);
    }

    private sealed class FakeDirectRecordingService : IDirectRecordingService
    {
        public DirectRecordingRequest? Request { get; private set; }
        public DirectRecordingResult StartResult { get; set; } = new(true);
        public DirectRecordingResult StopResult { get; set; } = new(true);
        public DirectRecordingStatus StatusResult { get; set; } = new(1, false);

        public DirectRecordingResult Start(DirectRecordingRequest request)
        {
            Request = request;
            return StartResult;
        }

        public Task<DirectRecordingResult> StopAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(StopResult);
        }

        public DirectRecordingStatus GetStatus()
        {
            return StatusResult;
        }

        public void Dispose()
        {
        }
    }
}
