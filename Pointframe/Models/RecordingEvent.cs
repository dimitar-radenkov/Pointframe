namespace Pointframe.Models;

public sealed record RecordingEvent(
    int SchemaVersion,
    long Sequence,
    long RelativeTimestampMilliseconds,
    string EventType,
    RecordingEventPayload Payload);

public sealed record RecordingEventPayload(
    int? CaptureX = null,
    int? CaptureY = null,
    int? CaptureWidth = null,
    int? CaptureHeight = null,
    int? FramesPerSecond = null,
    bool? IsEnabled = null,
    bool? IsMuted = null,
    int? RedactionX = null,
    int? RedactionY = null,
    int? RedactionWidth = null,
    int? RedactionHeight = null,
    long? RedactionRevision = null,
    string? RedactionMode = null,
    string? RedactionOperation = null);

public sealed record RecordingEventTrackSummary(
    string SidecarPath,
    long EventCount,
    int SchemaVersion);
