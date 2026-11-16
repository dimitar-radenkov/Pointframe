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
        "desktop_audit_ui",
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

    // Typed failure codes the code base already produces: generic categories, the CLI's capture, management and
    // verification codes, and the MCP tools' structured error codes; the invalid-request codes are added below.
    // Anything else, including free text, is reported as "other".
    private static readonly HashSet<string> _errorCodes = new(StringComparer.Ordinal)
    {
        Other,
        "cancelled",
        "canceled",
        "timeout",
        "io_error",
        "network_error",
        "access_denied",
        "invalid_params",
        "infrastructure_failure",
        "mcp_exited",
        "capture_failed",
        "target_not_found",
        "target_not_capturable",
        "monitor_not_found",
        "invalid_capture_bounds",
        "catalog_unavailable",
        "recording_already_active",
        "recording_not_active",
        "recording_start_failed",
        "recording_stop_failed",
        "artifact_changed",
        "artifact_missing",
        "artifact_not_found",
        "artifact_too_large",
        "artifact_unreadable",
        "download_failed",
        "package_verification_failed",
        "client_config_invalid",
        "io_failed",
        "mcp_management_failed",
        "mcp_install_failed",
        "mcp_release_asset_missing",
        "mcp_not_found",
        "mcp_version_mismatch",
        "mcp_start_failed",
        "spec_invalid",
        "spec_untrusted",
        "no_app",
        "app_outside_project",
        "approver_unavailable",
        "trust_needs_terminal",
        "not_approved",
        "task_invalid",
        "task_not_found",
        "task_exists",
        "task_id_invalid",
        "task_file_not_found",
        "task_not_covered",
        "scenario_not_found",
        "hook_error",
        "pointframe_not_on_path",
        "smoke_tools_missing",
        "smoke_app_not_listed",
        "smoke_failed",
        "app_not_found",
        "app_running",
        "desktop_busy",
        "client_not_configured",
        "handshake_failed",
        "package_not_installed",
        "unexpected_tool_surface",
        "examiner_failed",
        "examiner_invalid",
        "fail_before_rejected",
        "replace_not_approved",
        "ActionIdConflict",
        "AmbiguousTarget",
        "AttachNotImplemented",
        "CleanupIncomplete",
        "DesktopBusy",
        "DesktopUnavailable",
        "DispatchFailed",
        "DispatchTimeout",
        "ElementNotFound",
        "ExecutableMismatch",
        "FocusFailed",
        "FocusRequired",
        "GlobalHotkeyNotApproved",
        "HotKeyRegistrationFailed",
        "ImageIndexOutOfRange",
        "ImageNotFound",
        "InputDispatchFailed",
        "InputReleaseFailed",
        "InspectionIncomplete",
        "InspectionTimeout",
        "InvalidCondition",
        "InvalidPayload",
        "NativeInitializationFailed",
        "NegativeControlMatched",
        "NoRecordedCondition",
        "NoVisibleWindow",
        "ObservationNotInSession",
        "OcrEngineUnavailable",
        "PolicyUnavailable",
        "ProcessMismatch",
        "ProcessStateUnavailable",
        "ProfileNotFound",
        "ProofInvalid",
        "ProviderUnavailable",
        "QueueFull",
        "ReportNotFound",
        "ReportOutsideArtifactRoot",
        "ReportUnavailable",
        "SessionAlreadyExists",
        "SessionNotFound",
        "SessionPaused",
        "SnapshotTooLarge",
        "StaleObservation",
        "StaleObservationRef",
        "SurfaceOwnerMismatch",
        "TargetChanged",
        "TargetOccluded",
        "TargetStillRunning",
        "TargetUnavailable",
        "UiaUnavailable",
        "UnknownCriterion",
        "UnknownProfile",
        "UnsupportedCondition",
        "UnsupportedOperation",
        "UnsupportedSemanticValue",
        "UnsupportedSurface",
        "WindowOwnerMismatch",
        "WindowRefNotReplayable",
        "WindowUnavailable",
        "WorkerBusy",
    };

    static TelemetryAllowlist()
    {
        _errorCodes.UnionWith(InvalidRequestCodes.All);
    }

    public static IReadOnlyCollection<string> ErrorCodes => _errorCodes;

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
        TelemetryOutcome.VerificationFailed => "verification_failed",
        TelemetryOutcome.InvalidRequest => "invalid_request",
        _ => "error",
    };

    // The code comes from typed results and failure sites, never from message text; a value outside the
    // fixed set (including any free text) is reported as "other".
    public static string ErrorCode(string? code) => code is not null && _errorCodes.Contains(code) ? code : Other;

    // Maps an exception to a closed category; the exception type, message and stack never leave the machine.
    public static string ErrorCodeFor(Exception exception) => exception switch
    {
        OperationCanceledException => "cancelled",
        TimeoutException => "timeout",
        HttpRequestException => "network_error",
        UnauthorizedAccessException => "access_denied",
        IOException => "io_error",
        _ => Other,
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
