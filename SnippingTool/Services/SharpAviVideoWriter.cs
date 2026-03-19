using SharpAvi.Codecs;
using SharpAvi.Output;

namespace SnippingTool.Services;

public sealed class SharpAviVideoWriter : IVideoWriter
{
    private readonly AviWriter _writer;
    private readonly IAviVideoStream _stream;

    public SharpAviVideoWriter(int width, int height, int fps, string outputPath, int jpegQuality)
    {
        _writer = new AviWriter(outputPath) { FramesPerSecond = fps, EmitIndex1 = true };
        var encoder = new MJpegWpfVideoEncoder(width, height, jpegQuality);
        _stream = _writer.AddEncodingVideoStream(encoder, ownsEncoder: true, width, height);
    }

    public void WriteFrame(byte[] frameData) =>
        _stream.WriteFrame(true, frameData, 0, frameData.Length);

    public void Dispose() => _writer.Close();
}
