using System.Windows;
using Pointframe.Models;
using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class RecordingRedactionSessionTests
{
    [Fact]
    public void AddAndClear_PublishesSafeGeometryEventsAndReplacesSnapshot()
    {
        var eventTrack = new RecordingEventTrackStub();
        var session = new RecordingRedactionSession(eventTrack);
        var originalSnapshot = session.Snapshot();

        var region = session.Add(new Int32Rect(15, 25, 320, 180));
        var populatedSnapshot = session.Snapshot();
        var cleared = session.Clear();

        Assert.Empty(originalSnapshot.Span.ToArray());
        Assert.Equal(new Int32Rect(15, 25, 320, 180), region.CaptureLocalBounds);
        Assert.Equal(1, region.Revision);
        Assert.Equal(RecordingRedactionMode.Pixelate, region.Mode);
        Assert.Single(populatedSnapshot.Span.ToArray());
        Assert.True(cleared);
        Assert.Empty(session.Snapshot().Span.ToArray());
        Assert.Collection(eventTrack.Events,
            recordingEvent =>
            {
                Assert.Equal("redaction.added", recordingEvent.EventType);
                Assert.Equal(15, recordingEvent.Payload.RedactionX);
                Assert.Equal(25, recordingEvent.Payload.RedactionY);
                Assert.Equal(320, recordingEvent.Payload.RedactionWidth);
                Assert.Equal(180, recordingEvent.Payload.RedactionHeight);
                Assert.Equal(1, recordingEvent.Payload.RedactionRevision);
                Assert.Equal("pixelate", recordingEvent.Payload.RedactionMode);
                Assert.Equal("added", recordingEvent.Payload.RedactionOperation);
            },
            recordingEvent =>
            {
                Assert.Equal("redaction.removed", recordingEvent.EventType);
                Assert.Equal(2, recordingEvent.Payload.RedactionRevision);
                Assert.Equal("cleared", recordingEvent.Payload.RedactionOperation);
                Assert.Null(recordingEvent.Payload.RedactionX);
                Assert.Null(recordingEvent.Payload.RedactionY);
            });
    }

    private sealed class RecordingEventTrackStub : IRecordingEventTrack
    {
        public List<(string EventType, RecordingEventPayload Payload)> Events { get; } = [];

        public void Write(string eventType, RecordingEventPayload payload)
        {
            Events.Add((eventType, payload));
        }

        public RecordingEventTrackSummary Complete()
        {
            throw new NotSupportedException();
        }
    }
}