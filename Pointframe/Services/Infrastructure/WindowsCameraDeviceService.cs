using Windows.Devices.Enumeration;

namespace Pointframe.Services;

internal sealed class WindowsCameraDeviceService : ICameraDeviceService
{
    private readonly ILogger<WindowsCameraDeviceService> _logger;

    public WindowsCameraDeviceService(ILogger<WindowsCameraDeviceService> logger)
    {
        _logger = logger;
    }

    public async Task<IReadOnlyList<CameraDevice>> GetCamerasAsync()
    {
        try
        {
            var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture).AsTask().ConfigureAwait(true);
            return devices
                .Where(device => !string.IsNullOrEmpty(device.Id))
                .Select(device => new CameraDevice(device.Id, string.IsNullOrWhiteSpace(device.Name) ? "Camera" : device.Name))
                .ToList();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to enumerate cameras");
            return [];
        }
    }
}
