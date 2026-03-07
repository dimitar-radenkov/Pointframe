using SnippingTool.Models;

namespace SnippingTool.Services;

public interface IVideoWriterFactory
{
    IVideoWriter Create(RecordingFormat format, int width, int height, int fps, string outputPath);
}
