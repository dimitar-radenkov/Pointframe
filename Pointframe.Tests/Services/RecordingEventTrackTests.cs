using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Pointframe.Models;
using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class RecordingEventTrackTests
{
    [Fact]
    public void Write_WhenManyEventsAreQueued_AcceptsAllEvents()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pointframe-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var recordingPath = Path.Combine(directory, "recording.mp4");
        var track = new RecordingEventTrack(recordingPath, Stopwatch.StartNew());

        try
        {
            for (var i = 0; i < 256; i++)
            {
                track.Write("recording.progress", new RecordingEventPayload());
            }

            var summary = track.Complete();

            Assert.Equal(256, summary.EventCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Complete_WithLifecycleEvents_WritesOrderedUtf8JsonlAndFinalizesSidecar()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pointframe-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var recordingPath = Path.Combine(directory, "recording.mp4");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var track = new RecordingEventTrack(recordingPath, stopwatch);
            track.Write("recording.started", new RecordingEventPayload(
                CaptureX: 10,
                CaptureY: 20,
                CaptureWidth: 1280,
                CaptureHeight: 720,
                FramesPerSecond: 30,
                IsEnabled: true,
                IsMuted: false));
            track.Write("recording.paused", new RecordingEventPayload());
            track.Write("microphone.changed", new RecordingEventPayload(IsEnabled: true, IsMuted: true));
            track.Write("recording.stopped", new RecordingEventPayload());

            var summary = track.Complete();
            var bytes = File.ReadAllBytes(summary.SidecarPath);
            var events = File.ReadLines(summary.SidecarPath)
                .Select(line => JsonSerializer.Deserialize<RecordingEvent>(line))
                .ToArray();

            Assert.Equal($"{Path.GetFullPath(recordingPath)}.events.jsonl", summary.SidecarPath);
            Assert.Equal(4, summary.EventCount);
            Assert.Equal(RecordingEventTrack.CurrentSchemaVersion, summary.SchemaVersion);
            Assert.NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
            Assert.Collection(events,
                recordingEvent => AssertEvent(recordingEvent, 1, "recording.started"),
                recordingEvent => AssertEvent(recordingEvent, 2, "recording.paused"),
                recordingEvent => AssertEvent(recordingEvent, 3, "microphone.changed"),
                recordingEvent => AssertEvent(recordingEvent, 4, "recording.stopped"));
            Assert.All(events, recordingEvent =>
            {
                Assert.NotNull(recordingEvent);
                Assert.True(recordingEvent.RelativeTimestampMilliseconds >= 0);
                Assert.NotNull(recordingEvent.Payload);
            });

            File.AppendAllText(summary.SidecarPath, string.Empty);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void AssertEvent(RecordingEvent? recordingEvent, long sequence, string eventType)
    {
        Assert.NotNull(recordingEvent);
        Assert.Equal(RecordingEventTrack.CurrentSchemaVersion, recordingEvent.SchemaVersion);
        Assert.Equal(sequence, recordingEvent.Sequence);
        Assert.Equal(eventType, recordingEvent.EventType);
    }
}