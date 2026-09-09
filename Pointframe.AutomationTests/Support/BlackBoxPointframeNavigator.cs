using System.Text.Json;
using Pointframe.Engine.Automation.Models;

namespace Pointframe.AutomationTests.Support;

public sealed class BlackBoxPointframeNavigator
{
    public const string ListAppsTool = "desktop_list_apps";
    public const string StartSessionTool = "desktop_start_test_session";
    public const string RestartAppTool = "desktop_restart_app";
    public const string ObserveAppTool = "desktop_observe_app";
    public const string FocusWindowTool = "desktop_focus_window";
    public const string ClickTool = "desktop_click";
    public const string PressKeysTool = "desktop_press_keys";
    public const string CheckUiTool = "desktop_check_ui";
    public const string GetActionResultTool = "desktop_get_action_result";
    public const string GetReportTool = "desktop_get_test_report";
    public const string EndSessionTool = "desktop_end_test_session";

    private readonly McpDesktopTestClient _client;
    private readonly BlackBoxAppProfile _profile;
    private readonly bool _useAutomationIds;

    public BlackBoxPointframeNavigator(
        McpDesktopTestClient client,
        BlackBoxAppProfile profile,
        McpRunManifest manifest,
        bool useAutomationIds = true)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _useAutomationIds = useAutomationIds;
        Manifest.ActualArguments = profile.Arguments;
        Manifest.OrdinaryStartup = profile.Arguments.Count == 0;
        Manifest.CaptureTargetHashBefore(profile.ExecutablePath);
    }

    public McpRunManifest Manifest { get; }

    public bool UseAutomationIds => _useAutomationIds;

    public BlackBoxAppProfile Profile => _profile;

    public Task<JsonElement> ListAppsAsync(CancellationToken cancellationToken = default) =>
        CallAllowedAsync(DesktopTestingAction.ListApps, ListAppsTool, new { }, cancellationToken);

    public Task<JsonElement> StartSessionAsync(CancellationToken cancellationToken = default) =>
        CallAllowedAsync(DesktopTestingAction.StartTestSession, StartSessionTool, new { profileId = _profile.Id }, cancellationToken);

    public Task<JsonElement> RestartAppAsync(CancellationToken cancellationToken = default) =>
        CallAllowedAsync(DesktopTestingAction.RestartApp, RestartAppTool, new { profileId = _profile.Id }, cancellationToken);

    public async Task<JsonElement> ObserveAppAsync(CancellationToken cancellationToken = default)
    {
        var result = await CallAllowedAsync(
            DesktopTestingAction.ObserveApp,
            ObserveAppTool,
            new
            {
                profileId = _profile.Id,
                includeAutomationIds = _useAutomationIds,
            },
            cancellationToken).ConfigureAwait(false);
        Manifest.InitialObservedState = result.GetRawText();
        return result;
    }

    public Task<JsonElement> FocusWindowAsync(string windowRef, CancellationToken cancellationToken = default) =>
        CallAllowedAsync(DesktopTestingAction.FocusWindow, FocusWindowTool, new { windowRef }, cancellationToken);

    public Task<JsonElement> ClickAsync(
        DesktopTarget target,
        int clickCount = 1,
        CancellationToken cancellationToken = default) =>
        CallAllowedAsync(
            DesktopTestingAction.Click,
            ClickTool,
            new { target, clickCount },
            cancellationToken);

    public Task<JsonElement> PressKeysAsync(
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken = default) =>
        CallAllowedAsync(DesktopTestingAction.PressKeys, PressKeysTool, new { keys }, cancellationToken);

    public Task<JsonElement> CheckUiAsync(
        object predicate,
        CancellationToken cancellationToken = default) =>
        CallAllowedAsync(DesktopTestingAction.CheckUi, CheckUiTool, new { predicate }, cancellationToken);

    public Task<JsonElement> GetActionResultAsync(
        string actionId,
        CancellationToken cancellationToken = default) =>
        CallAllowedAsync(DesktopTestingAction.GetActionResult, GetActionResultTool, new { actionId }, cancellationToken);

    public Task<JsonElement> GetReportAsync(CancellationToken cancellationToken = default) =>
        CallAllowedAsync(DesktopTestingAction.GetTestReport, GetReportTool, new { }, cancellationToken);

    public async Task<JsonElement> EndSessionAsync(CancellationToken cancellationToken = default)
    {
        var result = await CallAllowedAsync(
            DesktopTestingAction.EndTestSession,
            EndSessionTool,
            new { },
            cancellationToken).ConfigureAwait(false);
        Manifest.CaptureTargetHashAfter();
        Manifest.Complete();
        return result;
    }

    private Task<JsonElement> CallAllowedAsync(
        DesktopTestingAction action,
        string tool,
        object arguments,
        CancellationToken cancellationToken)
    {
        if (!_profile.AllowedActions.Contains(action))
        {
            throw new InvalidOperationException($"Profile '{_profile.Id}' does not allow '{action}'.");
        }

        return _client.CallToolAsync(tool, arguments, cancellationToken: cancellationToken);
    }
}
