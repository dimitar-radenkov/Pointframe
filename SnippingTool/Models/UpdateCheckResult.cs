namespace SnippingTool.Models;

public sealed record UpdateCheckResult(
    bool IsUpdateAvailable,
    Version LatestVersion,
    string DownloadUrl);
