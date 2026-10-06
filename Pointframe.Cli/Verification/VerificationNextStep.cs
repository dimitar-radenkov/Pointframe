namespace Pointframe.Cli;

internal sealed record VerificationNextStep(string Kind, string? Command, int? RetryAfterSeconds, string Text);

internal static class VerificationNextSteps
{
    internal static VerificationNextStep For(string? status, string? errorCode, string? message = null, string? failureId = null, string? appBuildCommand = null, string? taskId = null)
    {
        if (status == VerificationStatus.Pass || status == "fresh")
        {
            return new("none", null, null, "No further action is needed.");
        }

        return errorCode switch
        {
            "mcp_not_found" or "mcp_version_mismatch" => new("run_command", "pointframe mcp install --client vscode", null, "Install the VS Code MCP server, then retry verification."),
            "desktop_busy" => new("wait", null, 30, "Wait for the desktop session to become available, then retry."),
            "app_not_found" => appBuildCommand is not null ? new("run_command", appBuildCommand, null, "Build the app, then retry verification.") : new("run_command", null, null, "Build the app first."),
            "app_running" => new("fix_code", null, null, "Close the running app, then retry verification."),
            "spec_untrusted" or "approver_unavailable" or "not_approved" or "not_trusted" or "trust_needs_terminal" or "replace_not_approved" => new("needs_person", "pointframe verify trust", null, "Ask a person to approve the verification commands with `pointframe verify trust`."),
            "spec_invalid" or "scenario_not_found" or "task_invalid" or "task_file_not_found" or "task_id_invalid" or "task_exists" => new("fix_code", null, null, $"Fix the spec or task file: {message ?? errorCode}."),
            "task_not_found" or "task_not_covered" or "verdict_for_another_task" => new("run_command", taskId is null ? "pointframe verify run --task <active id>" : $"pointframe verify run --task {taskId}", null, "Run verification for the active task."),
            "tree_changed" or "verdict_for_another_spec" => new("run_command", "pointframe verify run", null, "Re-run verification for the current files and spec."),
            "examiner_failed" or "examiner_invalid" or "fail_before_rejected" => new("needs_person", null, null, message ?? "Review the examiner failure and decide how to proceed."),
            null when status == VerificationStatus.Fail => new("fix_code", null, null, failureId is null
                ? "Fix the failing gate/scenario shown above, then run `pointframe verify run`."
                : $"Fix failing gate/scenario '{failureId}' shown above, then run `pointframe verify run`."),
            _ => new("needs_person", null, null, message ?? $"Review verification outcome '{errorCode ?? status}'.")
        };
    }
}
