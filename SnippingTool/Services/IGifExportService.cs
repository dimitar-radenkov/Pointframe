namespace SnippingTool.Services;

public interface IGifExportService
{
    Task Export(string inputPath, string outputPath, int fps, CancellationToken ct = default);
}
