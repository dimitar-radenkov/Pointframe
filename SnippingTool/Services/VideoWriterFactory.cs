using Microsoft.Extensions.Logging;
using SnippingTool.Models;

namespace SnippingTool.Services;

public sealed class VideoWriterFactory : IVideoWriterFactory
{
    private readonly IUserSettingsService _settings;
    private readonly ILogger<FFMpegVideoWriter> _ffmpegLogger;

    public VideoWriterFactory(IUserSettingsService settings, ILogger<FFMpegVideoWriter> ffmpegLogger)
    {
        _settings = settings;
        _ffmpegLogger = ffmpegLogger;
    }

    public IVideoWriter Create(RecordingFormat format, int width, int height, int fps, string outputPath)
    {
        return format switch
        {
            RecordingFormat.Mp4 => new FFMpegVideoWriter(width, height, fps, outputPath, _ffmpegLogger),
            RecordingFormat.Avi => new SharpAviVideoWriter(width, height, fps, outputPath, _settings.Current.RecordingJpegQuality),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported recording format")
        };
    }
}
