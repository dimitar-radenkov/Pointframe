namespace Pointframe.Services;

public interface IRecordingEventTrack
{
    void Write(string eventType, RecordingEventPayload payload);
    RecordingEventTrackSummary Complete();
}
