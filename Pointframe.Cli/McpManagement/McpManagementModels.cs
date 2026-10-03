namespace Pointframe.Cli;

internal sealed record McpPackageDownload(byte[] Bundle, string ChecksumText);

internal sealed record McpInstallation(string Version, string ExecutablePath, string InstallDirectory);

internal sealed record McpHealthResult(bool Success, string Code, string Message, IReadOnlyList<string>? Tools = null);

internal sealed record McpCommandResponse(
    int SchemaVersion,
    bool Success,
    string Action,
    string Client,
    string? Code,
    string Message,
    string? Version = null,
    string? ExecutablePath = null,
    string? ConfigurationPath = null,
    IReadOnlyList<string>? Tools = null,
    bool DryRun = false);
