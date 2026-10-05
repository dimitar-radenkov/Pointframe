using Pointframe.Engine;

namespace Pointframe.Services;

internal sealed class WelcomeEligibilityPolicy
{
    private static readonly HashSet<string> CaptureExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".mp4", ".gif",
    };

    private readonly IUserSettingsService _settings;
    private readonly ICaptureCatalogService _catalog;
    private readonly ICaptureLibrarySources _sources;

    public WelcomeEligibilityPolicy(
        IUserSettingsService settings,
        ICaptureCatalogService catalog,
        ICaptureLibrarySources sources)
    {
        _settings = settings;
        _catalog = catalog;
        _sources = sources;
    }

    public async Task<bool> ShouldShowAsync(bool automationMode, CancellationToken cancellationToken = default)
    {
        var settings = _settings.Current;
        if (automationMode || settings.FirstCaptureCompletedTracked || settings.FirstRecordingCompletedTracked || settings.WelcomeShown)
        {
            return false;
        }

        try
        {
            if (await _catalog.HasAnyCaptureAsync(cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            foreach (var root in _sources.GetImportRoots())
            {
                try
                {
                    if (Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
                        .Any(path => CaptureExtensions.Contains(Path.GetExtension(path))))
                    {
                        return false;
                    }
                }
                catch (DirectoryNotFoundException)
                {
                    // A not-yet-created screenshot folder contains no legacy captures.
                }
                catch (FileNotFoundException)
                {
                    // A root removed during startup contains no legacy captures.
                }

            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }

        return true;
    }
}
