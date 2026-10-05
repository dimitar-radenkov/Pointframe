using System.Text.Json;

namespace Pointframe.Telemetry;

public sealed class TelemetryPreferences
{
    public const string OptOutEnvironmentVariable = "POINTFRAME_TELEMETRY_OPTOUT";
    public const string DoNotTrackEnvironmentVariable = "DO_NOT_TRACK";
    public const string ConfigFileName = "agent-telemetry.json";
    public const string NoticeFileName = "agent-telemetry-notice-v1";

    private readonly string _directory;
    private readonly Func<string, string?> _getEnvironmentVariable;

    public TelemetryPreferences(string directory, Func<string, string?>? getEnvironmentVariable = null)
    {
        _directory = directory;
        _getEnvironmentVariable = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
    }

    public string ConfigPath => Path.Combine(_directory, ConfigFileName);

    public string NoticePath => Path.Combine(_directory, NoticeFileName);

    public bool NoticeShown => File.Exists(NoticePath);

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
