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

    public bool Remove(RecordingRedactionRegion region)
    {
        lock (_mutationLock)
        {
            var index = Array.FindIndex(_regions, existing => existing.Revision == region.Revision);
            if (index < 0)
            {
                return false;
            }

            RemoveAt(index);
            _eventTrack.Write("redaction.removed", CreatePayload(region, "removed"));
            return true;
        }
    }

    public bool Restore(RecordingRedactionRegion region)
    {
        lock (_mutationLock)
        {
            if (_regions.Any(existing => existing.Revision == region.Revision))
            {
                return false;
            }

            var updatedRegions = new RecordingRedactionRegion[_regions.Length + 1];
            Array.Copy(_regions, updatedRegions, _regions.Length);
            updatedRegions[^1] = region;
            var updatedPixelBounds = new PixelBounds[_pixelBounds.Length + 1];
            Array.Copy(_pixelBounds, updatedPixelBounds, _pixelBounds.Length);
            updatedPixelBounds[^1] = ToPixelBounds(region.CaptureLocalBounds);
            Volatile.Write(ref _regions, updatedRegions);
            Volatile.Write(ref _pixelBounds, updatedPixelBounds);
            _eventTrack.Write("redaction.added", CreatePayload(region, "restored"));
            return true;
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

    private void RemoveAt(int index)
    {
        var updatedRegions = new RecordingRedactionRegion[_regions.Length - 1];
        var updatedPixelBounds = new PixelBounds[_pixelBounds.Length - 1];
        Array.Copy(_regions, 0, updatedRegions, 0, index);
        Array.Copy(_regions, index + 1, updatedRegions, index, updatedRegions.Length - index);
        Array.Copy(_pixelBounds, 0, updatedPixelBounds, 0, index);
        Array.Copy(_pixelBounds, index + 1, updatedPixelBounds, index, updatedPixelBounds.Length - index);
        Volatile.Write(ref _regions, updatedRegions);
        Volatile.Write(ref _pixelBounds, updatedPixelBounds);
    }

    private static PixelBounds ToPixelBounds(Int32Rect bounds)
    {
        return new PixelBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height);
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
