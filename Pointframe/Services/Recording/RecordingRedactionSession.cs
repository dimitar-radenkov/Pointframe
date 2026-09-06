using System.Windows;
using Pointframe.Engine;

namespace Pointframe.Services;

public sealed class RecordingRedactionSession : IRecordingRedactionSession
{
    private readonly object _mutationLock = new();
    private readonly IRecordingEventTrack _eventTrack;
    private RecordingRedactionRegion[] _regions = [];
    private PixelBounds[] _pixelBounds = [];
    private long _revision;

    public RecordingRedactionSession(IRecordingEventTrack eventTrack)
    {
        _eventTrack = eventTrack;
    }

    public ReadOnlyMemory<RecordingRedactionRegion> Snapshot() => Volatile.Read(ref _regions);

    public ReadOnlyMemory<PixelBounds> SnapshotPixelBounds() => Volatile.Read(ref _pixelBounds);

    public RecordingRedactionRegion Add(Int32Rect captureLocalBounds)
    {
        if (captureLocalBounds.Width <= 0 || captureLocalBounds.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(captureLocalBounds));
        }

        lock (_mutationLock)
        {
            var region = new RecordingRedactionRegion(
                captureLocalBounds,
                ++_revision,
                RecordingRedactionMode.Pixelate);
            var existingRegions = _regions;
            var updatedRegions = new RecordingRedactionRegion[existingRegions.Length + 1];
            Array.Copy(existingRegions, updatedRegions, existingRegions.Length);
            updatedRegions[^1] = region;
            var existingPixelBounds = _pixelBounds;
            var updatedPixelBounds = new PixelBounds[existingPixelBounds.Length + 1];
            Array.Copy(existingPixelBounds, updatedPixelBounds, existingPixelBounds.Length);
            updatedPixelBounds[^1] = new PixelBounds(captureLocalBounds.X, captureLocalBounds.Y, captureLocalBounds.Width, captureLocalBounds.Height);
            Volatile.Write(ref _regions, updatedRegions);
            Volatile.Write(ref _pixelBounds, updatedPixelBounds);
            _eventTrack.Write("redaction.added", CreatePayload(region, "added"));
            return region;
        }
    }

    public bool Clear()
    {
        lock (_mutationLock)
        {
            if (_regions.Length == 0)
            {
                return false;
            }

            var revision = ++_revision;
            Volatile.Write(ref _regions, []);
            Volatile.Write(ref _pixelBounds, []);
            _eventTrack.Write("redaction.removed", new RecordingEventPayload(
                RedactionRevision: revision,
                RedactionOperation: "cleared"));
            return true;
        }
    }

    private static RecordingEventPayload CreatePayload(RecordingRedactionRegion region, string operation)
    {
        return new RecordingEventPayload(
            RedactionX: region.CaptureLocalBounds.X,
            RedactionY: region.CaptureLocalBounds.Y,
            RedactionWidth: region.CaptureLocalBounds.Width,
            RedactionHeight: region.CaptureLocalBounds.Height,
            RedactionRevision: region.Revision,
            RedactionMode: "pixelate",
            RedactionOperation: operation);
    }
}
