using System.Text.Json;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace Pointframe.Telemetry;

public static class OperationTelemetryFactory
{
    public const string ConnectionStringEnvironmentVariable = "POINTFRAME_TELEMETRY_CONNECTION_STRING";
    public const string PrivacyUrl = "https://dimitar-radenkov.github.io/Pointframe/privacy.html";

    public static string NoticeText(string configPath) =>
        "Pointframe sends anonymous usage counts from the CLI and MCP server: the command or tool name, " +
        "success or failure, a coarse duration bucket, cli or mcp, the MCP client type, and the version. " +
        "It never sends arguments, paths, screenshots, text, error messages, or any user, machine, or install identifier. " +
        "Application Insights masks IP addresses. " +
        $"Opt out with {TelemetryPreferences.OptOutEnvironmentVariable}=1 or {TelemetryPreferences.DoNotTrackEnvironmentVariable}=1, " +
        $"or put {{\"optOut\": true}} in {configPath}. Details: {PrivacyUrl}";

    public static IOperationTelemetry Create(
        TelemetryHost host,
        string version,
        string dataDirectory,
        Action<string>? showNotice = null)
    {
        try
        {
            var connectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable) is { Length: > 0 } overrideValue
                ? overrideValue
                : ReadEmbeddedConnectionString();
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return NullOperationTelemetry.Instance;
            }

            return Create(
                host,
                version,
                new TelemetryPreferences(dataDirectory),
                () => OperationTelemetry.CreateAzureExporter(connectionString),
                showNotice);
        }
        catch (Exception)
        {
            return NullOperationTelemetry.Instance;
        }
    }

    internal static IOperationTelemetry Create(
        TelemetryHost host,
        string version,
        TelemetryPreferences preferences,
        Func<BaseExporter<LogRecord>> exporterFactory,
        Action<string>? showNotice)
    {
        try
        {
            if (preferences.IsOptedOut())
            {
                return NullOperationTelemetry.Instance;
            }

            if (!preferences.NoticeShown)
            {
                showNotice?.Invoke(NoticeText(preferences.ConfigPath));
                preferences.MarkNoticeShown();
            }

            return new OperationTelemetry(host, version, exporterFactory);
        }
        catch (Exception)
        {
            return NullOperationTelemetry.Instance;
        }
    }

    public static string NormalizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return "0.0.0";
        }

        var parts = version.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var token = parts[^1];
        var plus = token.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            token = token[..plus];
        }

        return token.Length <= 32 ? token : token[..32];
    }

    private static string? ReadEmbeddedConnectionString()
    {
        using var stream = typeof(OperationTelemetryFactory).Assembly.GetManifestResourceStream("Pointframe.Telemetry.telemetry.json");
        if (stream is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("ApplicationInsights").GetProperty("ConnectionString").GetString();
    }
}
