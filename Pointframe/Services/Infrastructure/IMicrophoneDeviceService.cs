namespace Pointframe.Services;

public interface IMicrophoneDeviceService
{
    IReadOnlyList<string> GetAvailableCaptureDeviceNames();
    string? GetDefaultCaptureDeviceName();
    bool? TryGetCaptureDeviceMuted(string captureDeviceName);
    bool TrySetCaptureDeviceMuted(string captureDeviceName, bool isMuted);
}
