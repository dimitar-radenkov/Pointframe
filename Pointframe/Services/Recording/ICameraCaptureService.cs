namespace Pointframe.Services;

public sealed record CameraDevice(string Id, string Name);

public interface ICameraDeviceService
{
    Task<IReadOnlyList<CameraDevice>> GetCamerasAsync();
}

// One camera capture. StartAsync is awaited on the UI thread; FrameArrived and Failed are raised on worker threads.
// The frame passed to FrameArrived is owned by the handler, which must dispose it. StopAsync releases the camera,
// is idempotent, and is safe to call while StartAsync is still running.
public interface ICameraCaptureService
{
    event Action<CameraFrame>? FrameArrived;
    event Action<string>? Failed;

    // Returns null on success or one of CameraFailureReasons.
    Task<string?> StartAsync(string? deviceId);

    Task StopAsync();
}

public interface ICameraNoticeService
{
    void ReportUnavailable(string reason, string phase);
}

public interface IRecordingCameraControl
{
    bool IsOn { get; }

    event Action? StateChanged;

    Task SetOnAsync(bool on);
}

public interface IRecordingCameraSession : IRecordingCameraControl, IDisposable
{
    bool IsBubbleVisible { get; }

    // Raised on any thread, at most once until TryTakeFrame is called.
    event Action? FrameAvailable;

    // Raised on any thread when IsBubbleVisible changes.
    event Action? BubbleVisibilityChanged;

    Task StartAsync();

    CameraFrame? TryTakeFrame();
}
