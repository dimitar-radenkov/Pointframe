using Pointframe.Engine;

namespace Pointframe.Services;

internal sealed class WpfCaptureLibrarySources : ICaptureLibrarySources
{
    private readonly IUserSettingsService _settings;

    public WpfCaptureLibrarySources(IUserSettingsService settings)
    {
        _settings = settings;
    }

    public IReadOnlyList<string> GetImportRoots()
    {
        return new[]
        {
            _settings.Current.ScreenshotSavePath,
            PointframePaths.DefaultStandaloneScreenshotDirectory,
        }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
