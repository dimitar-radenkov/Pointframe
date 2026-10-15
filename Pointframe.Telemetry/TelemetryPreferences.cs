using System.Text.Json;

namespace Pointframe.Telemetry;

public sealed class TelemetryPreferences
{
    public const string OptOutEnvironmentVariable = "POINTFRAME_TELEMETRY_OPTOUT";
    public const string DoNotTrackEnvironmentVariable = "DO_NOT_TRACK";
    public const string ConfigFileName = "agent-telemetry.json";
    // v2: the notice changed when the anonymous install ID was added, so everyone sees it once more.
    public const string NoticeFileName = "agent-telemetry-notice-v2";
    public const string InstallIdFileName = "agent-install-id";

    private readonly string _directory;
    private readonly Func<string, string?> _getEnvironmentVariable;

    public TelemetryPreferences(string directory, Func<string, string?>? getEnvironmentVariable = null)
    {
        _directory = directory;
        _getEnvironmentVariable = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
    }

    public string ConfigPath => Path.Combine(_directory, ConfigFileName);

    public string NoticePath => Path.Combine(_directory, NoticeFileName);

    public string InstallIdPath => Path.Combine(_directory, InstallIdFileName);

    public bool NoticeShown => File.Exists(NoticePath);

    // A random ID, created on the first tracked operation and shared by the CLI and the MCP server of one user.
    // Null when it cannot be read or written; events are then sent without it.
    public string? GetOrCreateInstallId()
    {
        try
        {
            if (File.Exists(InstallIdPath))
            {
                var existing = File.ReadAllText(InstallIdPath).Trim();
                if (IsInstallId(existing))
                {
                    return existing;
                }
            }

            var created = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(_directory);
            File.WriteAllText(InstallIdPath, created);
            return created;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Opting out forgets the ID, so opting back in starts as a new, unrelated install.
    public void DeleteInstallId()
    {
        try
        {
            File.Delete(InstallIdPath);
        }
        catch (Exception)
        {
            // A leftover ID file is never sent while opted out.
        }
    }

    private static bool IsInstallId(string value)
    {
        return value.Length == 32 && value.All(Uri.IsHexDigit);
    }

    public bool IsOptedOut()
    {
        return IsTruthy(_getEnvironmentVariable(OptOutEnvironmentVariable))
            || IsTruthy(_getEnvironmentVariable(DoNotTrackEnvironmentVariable))
            || ConfigOptsOut();
    }

    public void MarkNoticeShown()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(NoticePath, "1");
    }

    private static bool IsTruthy(string? value)
    {
        return value is not null
            && (value.Equals("1", StringComparison.Ordinal)
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || value.Equals("on", StringComparison.OrdinalIgnoreCase));
    }

    // An unreadable or malformed config file counts as an opt-out: a person who wrote one meant to
    // configure this, and sending data against their intent is the worse failure.
    private bool ConfigOptsOut()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                return false;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(ConfigPath));
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name.Equals("optOut", StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value.ValueKind != JsonValueKind.False;
                }
            }

            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }
}
