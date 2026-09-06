namespace Pointframe.Services;

public interface IAudioExtractor
{
    Task<string> ExtractWavAsync(string videoPath, CancellationToken ct);
}
