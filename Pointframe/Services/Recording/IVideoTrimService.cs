namespace Pointframe.Services;

public interface IVideoTrimService
{
    Task Trim(string inputPath, string outputPath, TimeSpan start, TimeSpan end, CancellationToken ct = default);
}
