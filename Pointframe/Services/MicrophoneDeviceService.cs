using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Pointframe.Services;

public sealed class MicrophoneDeviceService : IMicrophoneDeviceService
{
    private readonly ILogger<MicrophoneDeviceService> _logger;

    public MicrophoneDeviceService(ILogger<MicrophoneDeviceService> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<string> GetAvailableCaptureDeviceNames()
    {
        try
        {
            var names = new List<string>();
            for (var index = 0; index < WaveIn.DeviceCount; index++)
            {
                var name = WaveIn.GetCapabilities(index).ProductName?.Trim();
                if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    names.Add(name);
                }
            }

            return names;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate microphone capture devices");
            return [];
        }
    }

    public string? GetDefaultCaptureDeviceName()
    {
        try
        {
            if (WaveIn.DeviceCount <= 0)
            {
                return null;
            }

            return WaveIn.GetCapabilities(-1).ProductName?.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve the default capture device for microphone recording");
            return null;
        }
    }

    public bool? TryGetCaptureDeviceMuted(string captureDeviceName)
    {
        try
        {
            using var endpoint = FindCaptureEndpoint(captureDeviceName);
            return endpoint?.AudioEndpointVolume.Mute;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read mute state for microphone device '{DeviceName}'", captureDeviceName);
            return null;
        }
    }

    public bool TrySetCaptureDeviceMuted(string captureDeviceName, bool isMuted)
    {
        try
        {
            using var endpoint = FindCaptureEndpoint(captureDeviceName);
            if (endpoint is null)
            {
                return false;
            }

            endpoint.AudioEndpointVolume.Mute = isMuted;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set mute state for microphone device '{DeviceName}' to {IsMuted}", captureDeviceName, isMuted);
            return false;
        }
    }

    private MMDevice? FindCaptureEndpoint(string captureDeviceName)
    {
        if (string.IsNullOrWhiteSpace(captureDeviceName))
        {
            return null;
        }

        using var enumerator = new MMDeviceEnumerator();
        return enumerator
            .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .OrderByDescending(device => GetMatchScore(device, captureDeviceName))
            .FirstOrDefault(device => GetMatchScore(device, captureDeviceName) > 0);
    }

    private static int GetMatchScore(MMDevice device, string captureDeviceName)
    {
        var friendlyName = device.FriendlyName?.Trim() ?? string.Empty;
        var deviceFriendlyName = device.DeviceFriendlyName?.Trim() ?? string.Empty;

        if (string.Equals(friendlyName, captureDeviceName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(deviceFriendlyName, captureDeviceName, StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        if ((!string.IsNullOrWhiteSpace(friendlyName)
                && friendlyName.Contains(captureDeviceName, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(deviceFriendlyName)
                && deviceFriendlyName.Contains(captureDeviceName, StringComparison.OrdinalIgnoreCase))
            || captureDeviceName.Contains(friendlyName, StringComparison.OrdinalIgnoreCase)
            || captureDeviceName.Contains(deviceFriendlyName, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return 0;
    }
}
