namespace Pointframe.Services.Messaging;

public sealed record RecordingCompletedMessage(
    string OutputPath,
    string ElapsedText,
    bool HadMicrophoneAudio,
    TimeSpan ElapsedDuration = default,
    RecordingSessionGeometry? Geometry = null,
    RecordingEventTrackSummary? EventTrackSummary = null);
