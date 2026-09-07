using System.Windows;

using Pointframe.Engine;

namespace Pointframe.Services;

public interface IRecordingRedactionSession
{
    ReadOnlyMemory<RecordingRedactionRegion> Snapshot();
    ReadOnlyMemory<PixelBounds> SnapshotPixelBounds();
    RecordingRedactionRegion Add(Int32Rect captureLocalBounds);
    bool Remove(RecordingRedactionRegion region);
    bool Restore(RecordingRedactionRegion region);
    bool Clear();
}
