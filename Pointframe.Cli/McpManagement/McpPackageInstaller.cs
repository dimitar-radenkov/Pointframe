using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Pointframe.Cli;

internal sealed class McpPackageInstaller : IMcpPackageInstaller
{
    private const string ExecutableName = "Pointframe.Mcp.exe";
    private const string StateFileName = "install-state.json";

    private readonly IMcpPackageSource _packageSource;
    private readonly string _rootDirectory;

    internal McpPackageInstaller(IMcpPackageSource packageSource, string rootDirectory)
    {
        _packageSource = packageSource;
        _rootDirectory = rootDirectory;
    }

    public McpInstallation? GetCurrent()
    {
        var statePath = Path.Combine(_rootDirectory, StateFileName);
        if (!File.Exists(statePath))
        {
            return null;
        }

        try
        {
            var state = JsonSerializer.Deserialize<McpInstallation>(File.ReadAllText(statePath));
            return state is not null && File.Exists(state.ExecutablePath) ? state : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<McpInstallation> InstallLatestAsync(bool dryRun, CancellationToken cancellationToken)
    {
        var package = await _packageSource.DownloadLatestAsync(cancellationToken);
        VerifyChecksum(package);

        var stagingParent = dryRun ? Path.GetTempPath() : _rootDirectory;
        var stagingDirectory = Path.Combine(stagingParent, $"Pointframe.Mcp.staging-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(stagingDirectory);
            var bundlePath = Path.Combine(stagingDirectory, "Pointframe.Mcp.mcpb");
            await File.WriteAllBytesAsync(bundlePath, package.Bundle, cancellationToken);
            ZipFile.ExtractToDirectory(bundlePath, stagingDirectory, overwriteFiles: true);
            File.Delete(bundlePath);

            var stagedExecutable = Path.Combine(stagingDirectory, ExecutableName);
            if (!File.Exists(stagedExecutable))
            {
                throw new InvalidDataException($"The MCP bundle does not contain {ExecutableName} at its root.");
            }

            var version = ReadManifestVersion(stagingDirectory);

            var installDirectory = Path.Combine(_rootDirectory, version);
            var installation = new McpInstallation(version, Path.Combine(installDirectory, ExecutableName), installDirectory);
            if (dryRun)
            {
                return installation;
            }

            Directory.CreateDirectory(_rootDirectory);
            if (!Directory.Exists(installDirectory))
            {
                Directory.Move(stagingDirectory, installDirectory);
            }

            if (!File.Exists(installation.ExecutablePath))
            {
                throw new InvalidDataException("The installed MCP executable is missing after activation.");
            }

            WriteStateAtomically(installation);
            return installation;
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
    }

    private static string ReadManifestVersion(string stagingDirectory)
    {
        var manifestPath = Path.Combine(stagingDirectory, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new InvalidDataException("The MCP bundle does not contain manifest.json at its root.");
        }

        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (!manifest.RootElement.TryGetProperty("version", out var versionElement)
            || versionElement.GetString() is not { Length: > 0 } version)
        {
            throw new InvalidDataException("The MCP bundle manifest does not contain a version.");
        }

        return version;
    }

    private static void VerifyChecksum(McpPackageDownload package)
    {
        var expected = package.ChecksumText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (expected is null || expected.Length != 64)
        {
            throw new InvalidDataException("The published MCP checksum file is invalid.");
        }

        var actual = Convert.ToHexStringLower(SHA256.HashData(package.Bundle));
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The downloaded MCP bundle does not match its published SHA-256 checksum.");
        }
    }

    private void WriteStateAtomically(McpInstallation installation)
    {
        var statePath = Path.Combine(_rootDirectory, StateFileName);
        var temporaryPath = $"{statePath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(installation));
        File.Move(temporaryPath, statePath, overwrite: true);
    }
}
