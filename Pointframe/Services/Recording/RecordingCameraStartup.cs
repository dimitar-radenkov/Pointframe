namespace Pointframe.Services;

// Shared by both recording entry points. With the setting off nothing is created and no camera API is touched.
internal static class RecordingCameraStartup
{
    public static async Task<IRecordingCameraSession?> BeginAsync(
        IUserSettingsService userSettings,
        Func<IRecordingCameraSession>? sessionFactory,
        ILogger logger)
    {
        if (sessionFactory is null || !userSettings.Current.RecordCamera)
        {
            return null;
        }

        IRecordingCameraSession? session = null;
        try
        {
            session = sessionFactory();
            await session.StartAsync().ConfigureAwait(true);
            return session;
        }
        catch (Exception exception)
        {
            // The camera is optional: a failure here must never stop the recording from starting.
            logger.LogWarning(exception, "Starting the recording camera failed");
            session?.Dispose();
            return null;
        }
    }

    public static string TelemetryValue(IUserSettingsService userSettings)
    {
        return userSettings.Current.RecordCamera ? "true" : "false";
    }
}
