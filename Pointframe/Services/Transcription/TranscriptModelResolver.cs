namespace Pointframe.Services;

internal static class TranscriptModelResolver
{
    internal const string ModelFileName = "ggml-base.en.bin";
    private const string ModelPathOverrideKey = "Pointframe.WhisperModelPath";

    // The per-user model folder. Unlike the installer's {app}\models it survives
    // rebuilds, clean checkouts and reinstalls, so it is where an in-app download
    // puts the model and where a manually downloaded one can be dropped.
    internal static string UserModelDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Pointframe",
        "models");

    internal static string UserModelPath => Path.Combine(UserModelDirectory, ModelFileName);

    internal static string? ResolveModelPath()
    {
        // The override still has to point at a real file: returning a stale or
        // mistyped path would make callers believe a model is installed and fail
        // inside Whisper instead of skipping gracefully.
        if (AppContext.GetData(ModelPathOverrideKey) is string overridePath
            && !string.IsNullOrWhiteSpace(overridePath)
            && File.Exists(overridePath))
        {
            return overridePath;
        }

        var appDir = AppContext.BaseDirectory;

        // 1. Look in a models subfolder next to the application binary (installer layout)
        var candidate = Path.Combine(appDir, "models", ModelFileName);
        if (File.Exists(candidate))
        {
            return candidate;
        }

        // 2. Look directly next to the application binary (dev layout)
        candidate = Path.Combine(appDir, ModelFileName);
        if (File.Exists(candidate))
        {
            return candidate;
        }

        // 3. Look in the per-user data folder (in-app download target)
        candidate = UserModelPath;
        if (File.Exists(candidate))
        {
            return candidate;
        }

        return null;
    }
}
