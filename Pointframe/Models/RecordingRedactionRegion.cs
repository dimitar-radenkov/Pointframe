using System.Windows;

namespace Pointframe.Models;

public enum RecordingRedactionMode
{
    Pixelate,
}

public sealed record RecordingRedactionRegion(
    Int32Rect CaptureLocalBounds,
    long Revision,
    RecordingRedactionMode Mode);
