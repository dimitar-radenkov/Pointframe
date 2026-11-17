namespace Pointframe.Services;

public static class CameraFailureReasons
{
    public const string NoDevice = "no_device";
    public const string DeviceMissing = "device_missing";
    public const string AccessDenied = "access_denied";
    public const string Busy = "busy";
    public const string Lost = "lost";
    public const string Stalled = "stalled";
    public const string Other = "other";

    public const string PhaseStart = "start";
    public const string PhaseCapture = "capture";

    public static string Normalize(string? reason)
    {
        return reason switch
        {
            NoDevice or DeviceMissing or AccessDenied or Busy or Lost or Stalled => reason,
            _ => Other,
        };
    }

    public static string FromException(Exception exception)
    {
        if (exception is UnauthorizedAccessException)
        {
            return AccessDenied;
        }

        var hresult = (uint)exception.HResult;
        return hresult switch
        {
            0x80070005 => AccessDenied,
            // MF_E_HW_MFT_FAILED_START_STREAMING and ERROR_BUSY: another app holds the camera exclusively.
            0xC00D3704 or 0x800700AA => Busy,
            // ERROR_NOT_FOUND / ERROR_FILE_NOT_FOUND / ERROR_DEVICE_NOT_CONNECTED
            0x80070490 or 0x80070002 or 0x8007048F => NoDevice,
            _ => Other,
        };
    }
}
