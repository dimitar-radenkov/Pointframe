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

            // A candidate or source build's download (API-selected or the legacy alias fallback, which can lag) is never
            // installed over a server at or above the verified bundle's own version.
            if (!dryRun
                && package.NeverDowngrade
                && GetCurrent() is { } current
                && TryParseVersion(version, out var bundleVersion)
                && TryParseVersion(current.Version, out var currentVersion)
                && bundleVersion <= currentVersion)
            {
                return current;
            }

            var installDirectory = Path.Combine(_rootDirectory, version);
            var installation = new McpInstallation(version, Path.Combine(installDirectory, ExecutableName), installDirectory);
            if (dryRun)
            {
                return installation;
            }

            Directory.CreateDirectory(_rootDirectory);
            using var activationLock = AcquireActivationLock();
            if (Directory.Exists(installDirectory) && !File.Exists(installation.ExecutablePath))
            {
                Directory.Delete(installDirectory, recursive: true);
            }

            if (!Directory.Exists(installDirectory))
            {
                try
                {
                    Directory.Move(stagingDirectory, installDirectory);
                }
                catch (IOException) when (File.Exists(installation.ExecutablePath))
                {
                    // Another process activated the same version first; its install is valid, so use it.
                }
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

    // Activation is synchronous (no awaits between acquire and release), so the mutex stays on one thread. It is
    // per user session and keyed by the install root, so concurrent first-time runs serialize instead of racing
    // the exists-check and Directory.Move.
    private ActivationLock AcquireActivationLock()
    {
        var key = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(_rootDirectory).ToUpperInvariant())))[..32];
        var mutex = new Mutex(false, $"Local\\Pointframe.Mcp.Install.{key}");
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromMinutes(2)))
            {
                mutex.Dispose();
                throw new IOException("Another Pointframe MCP installation is still running; try again in a moment.");
            }
        }
        catch (AbandonedMutexException)
        {
            // The previous holder ended without releasing; this thread now owns the lock.
        }

        return new ActivationLock(mutex);
    }

    private sealed class ActivationLock(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
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

    private static bool TryParseVersion(string text, out Version version)
    {
        var parts = text.Split('-', '+')[0].Split('.');
        if (parts.Length >= 3 && Version.TryParse(string.Join('.', parts.Take(3)), out var parsed))
        {
            version = parsed;
            return true;
        }

        version = new Version(0, 0, 0);
        return false;
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
