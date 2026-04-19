using Microsoft.Extensions.Logging;

namespace Pointframe.Services;

public sealed class VideoWriterFactory : IVideoWriterFactory
{
    private readonly ILogger<FFMpegVideoWriter> _ffmpegLogger;

    public VideoWriterFactory(ILogger<FFMpegVideoWriter> ffmpegLogger)
    {
        _ffmpegLogger = ffmpegLogger;
    }

    public IVideoWriter Create(int width, int height, int fps, string outputPath, string? microphoneDeviceName)
    {
        return new FFMpegVideoWriter(width, height, fps, outputPath, _ffmpegLogger, microphoneDeviceName);
    }
}
