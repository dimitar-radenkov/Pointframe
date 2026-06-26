namespace Pointframe.Services;

public sealed class VideoWriterFactory : IVideoWriterFactory
{
    private readonly ILogger<FFMpegVideoWriter> _ffmpegLogger;
    private readonly IUserSettingsService _settingsService;

    public VideoWriterFactory(ILogger<FFMpegVideoWriter> ffmpegLogger, IUserSettingsService settingsService)
    {
        _ffmpegLogger = ffmpegLogger;
        _settingsService = settingsService;
    }

    public IVideoWriter Create(int width, int height, int fps, string outputPath, string? microphoneDeviceName)
    {
        var currentSettings = _settingsService.Current;
        WatermarkSettings watermark = currentSettings.VideoWatermark is not null
            ? currentSettings.VideoWatermark
            : currentSettings.ScreenshotWatermark;
        return new FFMpegVideoWriter(width, height, fps, outputPath, _ffmpegLogger, microphoneDeviceName, watermark, DateTimeOffset.Now);
    }
}
