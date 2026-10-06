namespace Pointframe.Telemetry;

public static class TelemetryAllowlist
{
    public const string Other = "other";

    private static readonly HashSet<string> _cliCommands = new(StringComparer.Ordinal)
    {
        "displays",
        "windows",
        "capture",
        "ocr",
        "capture-window",
        "ocr-window",
        "record",
        "install",
        "mcp",
        "verify",
    };

    private static readonly HashSet<string> _mcpTools = new(StringComparer.Ordinal)
    {
        "search_captures",
        "get_capture",
        "list_displays",
        "list_windows",
        "capture_monitor",
        "capture_window",
        "read_text_from_monitor",
        "read_text_from_window",
        "start_recording",
        "stop_recording",
        "get_recording_status",
        "desktop_list_apps",
        "desktop_start_test_session",
        "desktop_restart_app",
        "desktop_observe_app",
        "desktop_focus_window",
        "desktop_click",
        "desktop_press_keys",
        "desktop_drag",
        "desktop_enter_text",
        "desktop_invoke",
        "desktop_check_ui",
        "desktop_replay_checks",
        "desktop_scroll",
        "desktop_get_action_result",
        "desktop_get_test_report",
        "desktop_end_test_session",
        "desktop_export_scenario",
    };

    public static IReadOnlyCollection<string> CliCommands => _cliCommands;

    public static IReadOnlyCollection<string> McpTools => _mcpTools;

    public static string OperationName(TelemetryHost host, string? name)
    {
        var allowed = host == TelemetryHost.Cli ? _cliCommands : _mcpTools;
        return name is not null && allowed.Contains(name) ? name : Other;
    }

    public static string OutcomeName(TelemetryOutcome outcome) => outcome switch
    {
        TelemetryOutcome.Success => "success",
        TelemetryOutcome.Cancelled => "cancelled",
        TelemetryOutcome.Denied => "denied",
        _ => "error",
    };

    public static string HostName(TelemetryHost host) => host == TelemetryHost.Cli ? "cli" : "mcp";

    public static string DurationBucket(TimeSpan duration)
    {
        if (duration < TimeSpan.FromSeconds(1))
        {
            return "lt_1s";
        }

        if (duration < TimeSpan.FromSeconds(5))
        {
            return "1_5s";
        }

        return duration <= TimeSpan.FromSeconds(30) ? "5_30s" : "gt_30s";
    }

    // The client name comes from the MCP initialize request, which any client can set to anything,
    // so only the fixed values below ever leave the machine.
    public static string McpClient(string? clientName)
    {
        if (string.IsNullOrWhiteSpace(clientName))
        {
            return Other;
        }

        var name = clientName.ToLowerInvariant();
        if (name.Contains("cursor", StringComparison.Ordinal))
        {
            return "cursor";
        }

        if (name.Contains("claude-code", StringComparison.Ordinal) || name.Contains("claude code", StringComparison.Ordinal))
        {
            return "claude-code";
        }

        if (name.Contains("claude", StringComparison.Ordinal))
        {
            return "claude-desktop";
        }

        if (name.Contains("codex", StringComparison.Ordinal))
        {
            return "codex";
        }

        return name.Contains("vscode", StringComparison.Ordinal) || name.Contains("visual studio code", StringComparison.Ordinal)
            ? "vscode"
            : Other;
    }
}
