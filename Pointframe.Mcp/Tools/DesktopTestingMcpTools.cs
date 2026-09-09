using System.ComponentModel;
using ModelContextProtocol.Server;
using Pointframe.Engine;
using Pointframe.Engine.Automation.Models;
using Pointframe.Engine.Automation.Services;
using Pointframe.Mcp.Automation;
using Pointframe.Mcp.Configuration;

namespace Pointframe.Mcp;

[McpServerToolType]
internal sealed class DesktopTestingMcpTools(
    IDesktopActionCoordinator coordinator,
    IDesktopTestSessionService sessions,
    IDesktopObservationService observations,
    IWindowsDesktopInputService input,
    IWindowsUiAutomationActionProvider uiAutomationActions,
    IDesktopUiCheckService checks,
    IDesktopOcrObservationProvider ocr,
    IDesktopTestReportService reports,
    DesktopTestingPolicyLoader policyLoader,
    DesktopTestingHostOptions options)
{
    [McpServerTool(Name = "desktop_list_apps", Title = "List desktop applications", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("Acknowledges a request for policy-eligible desktop application candidates. This host does not enumerate candidates: it always returns an empty acknowledgement, never a list of applications. To start an application, pass a profile id from the desktop testing policy file directly to desktop_start_test_session.")]
    public Task<DesktopTestingActionResponse> ListAppsAsync(
        [Description("A UUID action identifier.")] string actionId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Ok(actionId));

    [McpServerTool(Name = "desktop_start_test_session", Title = "Start desktop test session", UseStructuredContent = true)]
    [Description("Starts one approved desktop application profile or attaches to an explicitly approved process.")]
    public async Task<DesktopTestingActionResponse> StartTestSessionAsync(
        [Description("A UUID action identifier.")] string actionId,
        [Description("The policy profile to launch. Must match a profile id declared in the desktop testing policy file.")] string profileId,
        [Description("Reserved for attaching to an already-running approved process. Attaching is not implemented by this host; leave unset to launch the profile.")] string? appRef = null,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(appRef))
        {
            return Error(actionId, "AttachNotImplemented", "Attach candidates are not available from this host.");
        }

        var policy = LoadPolicy();
        var profile = policy.Profiles.SingleOrDefault(item => string.Equals(item.Id, profileId, StringComparison.Ordinal));
        if (profile is null)
        {
            return Error(actionId, "ProfileNotFound", $"Profile '{profileId}' was not found.");
        }

        var sessionRef = $"session-{Guid.NewGuid():N}";
        var result = await sessions.StartAsync(
            sessionRef,
            profile.Id,
            new DesktopLaunchRequest(profile.ExecutablePath, profile.Arguments, profile.WorkingDirectory),
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return Error(actionId, result.Code, result.Message);
        }

        reports.Initialize(
            sessionRef,
            result.Session!.Target!.Process.ExecutablePath,
            result.Session.Target.Process.ExecutableSha256);
        return Ok(actionId, sessionRef, result.Session.Target.TargetRef);
    }

    [McpServerTool(Name = "desktop_restart_app", Title = "Restart desktop application", UseStructuredContent = true)]
    [Description("Stops and relaunches the application under test for an existing session, using the same policy profile it was started with. The session id stays valid; observation and element references from before the restart do not.")]
    public async Task<DesktopTestingActionResponse> RestartAppAsync(
        [Description("The session id returned by desktop_start_test_session.")] string sessionId,
        [Description("A UUID action identifier.")] string actionId,
        CancellationToken cancellationToken = default)
    {
        var session = await sessions.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session?.Target is null)
        {
            return Error(actionId, "SessionNotFound", "The desktop test session was not found.");
        }

        var policy = LoadPolicy();
        var profile = policy.Profiles.Single(item => item.Id == session.ProfileId);
        var result = await sessions.RestartAsync(
            sessionId,
            new DesktopLaunchRequest(profile.ExecutablePath, profile.Arguments, profile.WorkingDirectory),
            cancellationToken).ConfigureAwait(false);
        return result.Succeeded
            ? Ok(actionId, sessionId, result.Session?.Target?.TargetRef)
            : Error(actionId, result.Code, result.Message);
    }

    [McpServerTool(Name = "desktop_observe_app", Title = "Observe desktop application", ReadOnly = true, UseStructuredContent = true)]
    [Description("Captures the current state of the application under test: one image block per requested rectangle, plus the UI Automation element tree when requested. This is the only way to obtain the observation_ref and image_ref that desktop_click, desktop_drag, desktop_enter_text, and desktop_scroll require, so call it before every interaction — refs expire 30 seconds after capture, and are also invalidated if the monitor topology changes, after which any action using them is rejected as StaleObservation.")]
    public async Task<DesktopTestingObservationResponse> ObserveAppAsync(
        [Description("The session id returned by desktop_start_test_session.")] string sessionId,
        [Description("Screen rectangles to capture, in absolute desktop physical pixels: at least 1 and at most 16. Each becomes one image block with its own image_ref.")] IReadOnlyList<McpPixelBounds> captureBoundsPixels,
        [Description("Whether to include the UI Automation element tree alongside the pixels. Elements carry the element_ref that desktop_invoke requires. Defaults to true.")] bool includeUiAutomation = true,
        [Description("Optional image_ref from this same observation to additionally run OCR over. Omit to skip OCR.")] string? ocrImageRef = null,
        CancellationToken cancellationToken = default)
    {
        var session = await sessions.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session?.Target is null)
        {
            // Every sibling tool reports an unknown session as a typed error. Returning null here left the
            // caller unable to distinguish "session gone" from "observed nothing".
            return SessionNotFoundObservation();
        }

        var result = await observations.ObserveAsync(
            new DesktopObservationRequest(
                session.Target.Process,
                captureBoundsPixels.Select(bounds => new PixelBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height)).ToArray(),
                includeUiAutomation),
            cancellationToken).ConfigureAwait(false);
        var response = DesktopTestingResponseMapper.MapObservation(result);
        if (string.IsNullOrWhiteSpace(ocrImageRef))
        {
            return response;
        }

        return DesktopTestingResponseMapper.WithOcr(
            response,
            await ocr.RecognizeAsync(result, ocrImageRef, cancellationToken).ConfigureAwait(false));
    }

    [McpServerTool(Name = "desktop_focus_window", Title = "Focus desktop window", UseStructuredContent = true)]
    [Description("Brings one window of the application under test to the foreground and gives it keyboard focus. Call this before desktop_press_keys when the target window may not already be focused, since key input goes to whatever currently has focus.")]
    public async Task<DesktopTestingActionResponse> FocusWindowAsync(
        [Description("The session id returned by desktop_start_test_session.")] string sessionId,
        [Description("A UUID action identifier.")] string actionId,
        [Description("The window reference (window_ref) of an element from a recent desktop_observe_app response.")] string windowRef,
        CancellationToken cancellationToken = default)
    {
        var session = await sessions.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session?.Target is null)
        {
            return Error(actionId, "SessionNotFound", "The desktop test session was not found.");
        }

        var target = new DesktopInputTarget(
            new DesktopWindowIdentity(windowRef, session.Target.Process.ProcessRef, nint.Zero));
        return await ExecuteInputAsync(
            sessionId,
            actionId,
            "focus_window",
            new { sessionId, windowRef },
            () => input.FocusAsync(target, session.Target.Process, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool(Name = "desktop_click", Title = "Click desktop target", UseStructuredContent = true)]
    [Description("Clicks a point inside a captured image from desktop_observe_app. Coordinates are image-local pixels relative to that image block's top-left corner, not desktop coordinates; the server maps them back to the desktop for you.")]
    public Task<DesktopTestingActionResponse> ClickAsync(
        [Description("The session id returned by desktop_start_test_session.")] string sessionId,
        [Description("A UUID action identifier.")] string actionId,
        [Description("The observation_ref from the desktop_observe_app response that produced the image below.")] string observationRef,
        [Description("The image_ref of the image block within that observation whose coordinate space x and y are expressed in.")] string imageRef,
        [Description("Horizontal position in image-local pixels, measured from the image block's left edge. Must be inside the image.")] int x,
        [Description("Vertical position in image-local pixels, measured from the image block's top edge. Must be inside the image.")] int y,
        [Description("Number of clicks: 1 for a single click, 2 for a double click. Values above 2 are rejected. Defaults to 1.")] int count = 1,
        [Description("Whether to click with the right mouse button instead of the left. Defaults to false.")] bool rightButton = false,
        CancellationToken cancellationToken = default) =>
        ClickCoreAsync(sessionId, actionId, observationRef, imageRef, x, y, count, rightButton, cancellationToken);

    [McpServerTool(Name = "desktop_press_keys", Title = "Press desktop keys", UseStructuredContent = true)]
    [Description("Presses a chord of keys, given as Windows virtual-key codes, against whatever currently has keyboard focus. Call desktop_focus_window first unless the target is already focused. Keys are pressed in the given order and released in reverse, so pass modifiers first (for example [0x11, 0x43] for Ctrl+C).")]
    public Task<DesktopTestingActionResponse> PressKeysAsync(
        [Description("The session id returned by desktop_start_test_session.")] string sessionId,
        [Description("A UUID action identifier.")] string actionId,
        [Description("One to four Windows virtual-key codes to press together, modifiers first (for example 0x11 Ctrl, 0x10 Shift, 0x12 Alt). More than four keys is rejected.")] IReadOnlyList<ushort> virtualKeys,
        [Description("Set only when the chord is a system-wide hotkey rather than input to the focused window. Must name a hotkey the active policy profile approves, or the action is rejected.")] string? globalHotkeyId = null,
        CancellationToken cancellationToken = default) =>
        PressKeysCoreAsync(sessionId, actionId, virtualKeys, globalHotkeyId, cancellationToken);

    [McpServerTool(Name = "desktop_drag", Title = "Drag desktop target", UseStructuredContent = true)]
    [Description("Presses the left mouse button at the first point, moves through the remaining points in order, and releases at the last. Requires at least two points. Coordinates are image-local pixels within the given observation image, as for desktop_click.")]
    public Task<DesktopTestingActionResponse> DragAsync(
        [Description("The session id returned by desktop_start_test_session.")] string sessionId,
        [Description("A UUID action identifier.")] string actionId,
        [Description("The observation_ref from the desktop_observe_app response that produced the image below.")] string observationRef,
        [Description("The image_ref of the image block within that observation whose coordinate space the points are expressed in.")] string imageRef,
        [Description("The drag path, in order, as image-local pixel positions: at least 2 and at most 128. Only x and y are used; width and height are ignored and may be zero.")] IReadOnlyList<McpPixelBounds> points,
        [Description("How long the whole drag should take, in milliseconds, from 50 through 5000. Longer drags are more reliable with animated or drag-threshold-sensitive UI. Defaults to 250.")] int durationMilliseconds = 250,
        CancellationToken cancellationToken = default) =>
        DragCoreAsync(sessionId, actionId, observationRef, imageRef, points, durationMilliseconds, cancellationToken);

    [McpServerTool(Name = "desktop_enter_text", Title = "Enter desktop text", UseStructuredContent = true)]
    [Description("Clicks a point to place the caret and then types text into it. Prefer semantic entry with an element_ref where the field exposes it, since that sets the value directly instead of relying on synthesized keystrokes.")]
    public Task<DesktopTestingActionResponse> EnterTextAsync(
        [Description("The session id returned by desktop_start_test_session.")] string sessionId,
        [Description("A UUID action identifier.")] string actionId,
        [Description("The observation_ref from the desktop_observe_app response that produced the image below.")] string observationRef,
        [Description("The image_ref of the image block within that observation whose coordinate space x and y are expressed in.")] string imageRef,
        [Description("Horizontal position of the text field in image-local pixels.")] int x,
        [Description("Vertical position of the text field in image-local pixels.")] int y,
        [Description("The literal text to enter. At most 4096 characters.")] string text,
        [Description("Set the field's value through UI Automation instead of typing keystrokes. Requires elementRef. Defaults to false.")] bool semanticValue = false,
        [Description("The element_ref of the target field from desktop_observe_app. Required when semanticValue is true.")] string? elementRef = null,
        CancellationToken cancellationToken = default) =>
        EnterTextCoreAsync(sessionId, actionId, observationRef, imageRef, x, y, text, semanticValue, elementRef, cancellationToken);

    [McpServerTool(Name = "desktop_invoke", Title = "Invoke desktop element", UseStructuredContent = true)]
    [Description("Activates a UI Automation element directly through its invoke pattern, without moving the mouse or synthesizing input. Prefer this over desktop_click for buttons and menu items that expose an element_ref: it does not depend on the element being visible, unoccluded, or correctly mapped from pixels.")]
    public async Task<DesktopTestingActionResponse> InvokeAsync(
        [Description("The session id returned by desktop_start_test_session.")] string sessionId,
        [Description("A UUID action identifier.")] string actionId,
        [Description("The element_ref of the element to invoke, from a recent desktop_observe_app response with includeUiAutomation enabled.")] string elementRef,
        CancellationToken cancellationToken = default)
    {
        var session = await sessions.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session?.Target is null)
        {
            return Error(actionId, "SessionNotFound", "The desktop test session was not found.");
        }

        if (string.IsNullOrWhiteSpace(elementRef))
        {
            return Error(actionId, "ElementRequired", "A verified UI automation element reference is required.");
        }

        return await ExecuteSemanticInputAsync(
            sessionId,
            actionId,
            "invoke",
            new { sessionId, elementRef },
            () => uiAutomationActions.TryInvoke(elementRef),
            cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool(Name = "desktop_check_ui", Title = "Check desktop UI", ReadOnly = true, UseStructuredContent = true)]
    [Description("Waits until a condition holds over the application's UI Automation state, or until the timeout elapses. Use this to synchronize with the UI after an action instead of guessing at delays. Returns verification 'passed', 'failed', or 'inconclusive' when the state could not be read at all.")]
    public async Task<DesktopTestingCheckResponse> CheckUiAsync(
        [Description("The session id returned by desktop_start_test_session.")] string sessionId,
        [Description("The condition to wait for.")] DesktopUiCheckCondition condition,
        [Description("How long to wait for the condition before giving up, in seconds. At most 30.")] int timeoutSeconds = DesktopTestingLimits.DefaultUiCheckTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        _ = sessionId;
        var evaluation = await checks.CheckAsync(condition, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken).ConfigureAwait(false);
        return DesktopTestingResponseMapper.MapCheck(evaluation);
    }

    [McpServerTool(Name = "desktop_scroll", Title = "Scroll desktop target", UseStructuredContent = true)]
    [Description("Scrolls the mouse wheel over a point inside a captured image from desktop_observe_app. Coordinates are image-local pixels, as for desktop_click.")]
    public Task<DesktopTestingActionResponse> ScrollAsync(
        [Description("The session id returned by desktop_start_test_session.")] string sessionId,
        [Description("A UUID action identifier.")] string actionId,
        [Description("The observation_ref from the desktop_observe_app response that produced the image below.")] string observationRef,
        [Description("The image_ref of the image block within that observation whose coordinate space x and y are expressed in.")] string imageRef,
        [Description("Horizontal position to scroll over, in image-local pixels.")] int x,
        [Description("Vertical position to scroll over, in image-local pixels.")] int y,
        [Description("Number of wheel detents (notches), from -10 through 10. Positive scrolls up, negative scrolls down.")] int detents,
        CancellationToken cancellationToken = default) =>
        ScrollCoreAsync(sessionId, actionId, observationRef, imageRef, x, y, detents, cancellationToken);

    [McpServerTool(Name = "desktop_get_action_result", Title = "Get desktop action result", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("Returns the recorded outcome of a previously dispatched action by its action id. Use this to resolve an action whose result was uncertain; actions are never replayed, so re-issuing the original call is not a safe alternative.")]
    public Task<DesktopTestingActionResponse> GetActionResultAsync(
        [Description("The UUID action identifier that was passed to the original action.")] string actionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(DesktopTestingResponseMapper.MapAction(coordinator.GetResult(actionId)));
    }

    [McpServerTool(Name = "desktop_get_test_report", Title = "Get desktop test report", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("Finalizes and returns the audit report for a session: the executable under test, its hash, and the ledger of every action dispatched. Call before desktop_end_test_session if the report is needed.")]
    public async Task<DesktopTestReport> GetTestReportAsync(
        [Description("The session id returned by desktop_start_test_session.")] string sessionId,
        CancellationToken cancellationToken = default)
    {
        return await coordinator.FinalizeAsync(sessionId, cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool(Name = "desktop_end_test_session", Title = "End desktop test session", UseStructuredContent = true)]
    [Description("Ends the session and stops the application it launched. Observation, image, and element references from the session become invalid. Always call this when finished, so the target process is not left running.")]
    public async Task<DesktopTestingActionResponse> EndTestSessionAsync(
        [Description("The session id returned by desktop_start_test_session.")] string sessionId,
        [Description("A UUID action identifier.")] string actionId,
        CancellationToken cancellationToken = default)
    {
        var result = await sessions.EndAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? Ok(actionId) : Error(actionId, result.Code, result.Message);
    }

    private static DesktopTestingObservationResponse SessionNotFoundObservation()
    {
        return new DesktopTestingObservationResponse(
            DesktopTestingLimits.SchemaVersion,
            ObservationRef: string.Empty,
            TargetState: nameof(DesktopTargetState.Unavailable),
            ObservationStatus: nameof(DesktopObservationStatus.Unavailable),
            UiaStatus: nameof(DesktopUiAutomationStatus.Unavailable),
            ProcessRef: null,
            IsTruncated: false,
            TopologyGeneration: 0,
            PixelCapturedUtc: default,
            UiaCapturedUtc: null,
            Images: [],
            Elements: [],
            Error: new McpCaptureError("SessionNotFound", "The desktop test session was not found."));
    }

    private DesktopTestingPolicy LoadPolicy()
    {
        if (string.IsNullOrWhiteSpace(options.PolicyPath))
        {
            throw new InvalidOperationException("Desktop testing requires an absolute policy path.");
        }

        return policyLoader.LoadAndValidate(options.PolicyPath);
    }

    private async Task<DesktopTestingActionResponse> ClickCoreAsync(
        string sessionId,
        string actionId,
        string observationRef,
        string imageRef,
        int x,
        int y,
        int count,
        bool rightButton,
        CancellationToken cancellationToken)
    {
        var session = await sessions.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session?.Target is null)
        {
            return Error(actionId, "SessionNotFound", "The desktop test session was not found.");
        }

        try
        {
            var observation = observations.Resolve(observationRef);
            var image = observation.Observation.Images.Single(item => item.ImageRef == imageRef);
            var point = observations.ToDesktopPixels(observationRef, imageRef, x, y);
            return await ExecuteInputAsync(
                sessionId,
                actionId,
                "click",
                new { sessionId, observationRef, imageRef, x, y, count, rightButton },
                () => input.ClickAsync(
                    new DesktopClickRequest(
                        new DesktopInputTarget(BoundsPixels: image.DesktopBoundsPixels),
                        point.X,
                        point.Y,
                        count,
                        rightButton),
                    session.Target.Process,
                    cancellationToken),
                cancellationToken,
                observation.Observation.ObservationRef).ConfigureAwait(false);
        }
        catch (DesktopOperationException exception)
        {
            return Error(actionId, exception.Code, exception.Message);
        }
    }

    private async Task<DesktopTestingActionResponse> PressKeysCoreAsync(
        string sessionId,
        string actionId,
        IReadOnlyList<ushort> virtualKeys,
        string? globalHotkeyId,
        CancellationToken cancellationToken)
    {
        var session = await sessions.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session?.Target is null)
        {
            return Error(actionId, "SessionNotFound", "The desktop test session was not found.");
        }

        if (!string.IsNullOrWhiteSpace(globalHotkeyId))
        {
            var profile = LoadPolicy().Profiles.SingleOrDefault(item => item.Id == session.ProfileId);
            if (profile is null || !profile.AllowedGlobalHotkeys.ContainsKey(globalHotkeyId))
            {
                return Error(actionId, "GlobalHotkeyNotApproved", "The global hotkey is not approved for the active profile.");
            }
        }

        var method = string.IsNullOrWhiteSpace(globalHotkeyId)
            ? DesktopInputMethod.Physical
            : DesktopInputMethod.GlobalHotkey;
        return await ExecuteInputAsync(
            sessionId,
            actionId,
            "press_keys",
            new { sessionId, virtualKeys, globalHotkeyId },
            () => input.PressKeysAsync(
                new DesktopKeyPressRequest(null, virtualKeys, method, globalHotkeyId),
                session.Target.Process,
                cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<DesktopTestingActionResponse> DragCoreAsync(
        string sessionId,
        string actionId,
        string observationRef,
        string imageRef,
        IReadOnlyList<McpPixelBounds> points,
        int durationMilliseconds,
        CancellationToken cancellationToken)
    {
        var session = await sessions.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session?.Target is null)
        {
            return Error(actionId, "SessionNotFound", "The desktop test session was not found.");
        }

        try
        {
            var observation = observations.Resolve(observationRef);
            var image = observation.Observation.Images.Single(item => item.ImageRef == imageRef);
            var mappedPoints = points.Select(point =>
            {
                var mapped = observations.ToDesktopPixels(observationRef, imageRef, point.X, point.Y);
                return new PixelBounds(mapped.X, mapped.Y, 1, 1);
            }).ToArray();
            return await ExecuteInputAsync(
                sessionId,
                actionId,
                "drag",
                new { sessionId, observationRef, imageRef, points, durationMilliseconds },
                () => input.DragAsync(
                    new DesktopDragRequest(
                        new DesktopInputTarget(BoundsPixels: image.DesktopBoundsPixels),
                        mappedPoints,
                        durationMilliseconds),
                    session.Target.Process,
                    cancellationToken),
                cancellationToken,
                observation.Observation.ObservationRef).ConfigureAwait(false);
        }
        catch (DesktopOperationException exception)
        {
            return Error(actionId, exception.Code, exception.Message);
        }
    }

    private async Task<DesktopTestingActionResponse> EnterTextCoreAsync(
        string sessionId,
        string actionId,
        string observationRef,
        string imageRef,
        int x,
        int y,
        string text,
        bool semanticValue,
        string? elementRef,
        CancellationToken cancellationToken)
    {
        var session = await sessions.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session?.Target is null)
        {
            return Error(actionId, "SessionNotFound", "The desktop test session was not found.");
        }

        try
        {
            var observation = observations.Resolve(observationRef);
            if (semanticValue)
            {
                if (string.IsNullOrWhiteSpace(elementRef))
                {
                    return Error(actionId, "ElementRequired", "Semantic ValuePattern entry requires a verified UI automation element reference.");
                }

                return await ExecuteSemanticInputAsync(
                    sessionId,
                    actionId,
                    "enter_text",
                    new { sessionId, observationRef, imageRef, text, semanticValue, elementRef },
                    () => uiAutomationActions.TrySetValue(elementRef, text),
                    cancellationToken,
                    observation.Observation.ObservationRef).ConfigureAwait(false);
            }

            var image = observation.Observation.Images.Single(item => item.ImageRef == imageRef);
            var point = observations.ToDesktopPixels(observationRef, imageRef, x, y);
            return await ExecuteInputAsync(
                sessionId,
                actionId,
                "enter_text",
                new { sessionId, observationRef, imageRef, x, y, text, semanticValue, elementRef },
                () => input.EnterTextAsync(
                    new DesktopTextRequest(
                        new DesktopInputTarget(BoundsPixels: image.DesktopBoundsPixels),
                        text,
                        semanticValue,
                        point.X,
                        point.Y),
                    session.Target.Process,
                    cancellationToken),
                cancellationToken,
                observation.Observation.ObservationRef).ConfigureAwait(false);
        }
        catch (DesktopOperationException exception)
        {
            return Error(actionId, exception.Code, exception.Message);
        }
    }

    private async Task<DesktopTestingActionResponse> ScrollCoreAsync(
        string sessionId,
        string actionId,
        string observationRef,
        string imageRef,
        int x,
        int y,
        int detents,
        CancellationToken cancellationToken)
    {
        var session = await sessions.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session?.Target is null)
        {
            return Error(actionId, "SessionNotFound", "The desktop test session was not found.");
        }

        try
        {
            var observation = observations.Resolve(observationRef);
            var image = observation.Observation.Images.Single(item => item.ImageRef == imageRef);
            _ = observations.ToDesktopPixels(observationRef, imageRef, x, y);
            return await ExecuteInputAsync(
                sessionId,
                actionId,
                "scroll",
                new { sessionId, observationRef, imageRef, x, y, detents },
                () => input.ScrollAsync(
                    new DesktopScrollRequest(
                        new DesktopInputTarget(BoundsPixels: image.DesktopBoundsPixels),
                        detents,
                        observations.ToDesktopPixels(observationRef, imageRef, x, y).X,
                        observations.ToDesktopPixels(observationRef, imageRef, x, y).Y),
                    session.Target.Process,
                    cancellationToken),
                cancellationToken,
                observation.Observation.ObservationRef).ConfigureAwait(false);
        }
        catch (DesktopOperationException exception)
        {
            return Error(actionId, exception.Code, exception.Message);
        }
    }

    private async Task<DesktopTestingActionResponse> ExecuteInputAsync(
        string sessionId,
        string actionId,
        string operation,
        object canonicalArguments,
        Func<Task<DesktopInputPreflightResult>> dispatch,
        CancellationToken cancellationToken,
        params string[] observationsToInvalidate)
    {
        var result = await coordinator.ExecuteAsync(
            sessionId,
            actionId,
            canonicalArguments,
            async _ =>
            {
                var preflight = await dispatch().ConfigureAwait(false);
                return preflight.IsValid
                    ? new DesktopActionExecution(DesktopDispatchStatus.Complete)
                    : new DesktopActionExecution(
                        DesktopDispatchStatus.NotStarted,
                        DesktopVerificationStatus.Failed,
                        DesktopObservationStatus.NotRequested,
                        new DesktopOperationError(preflight.Code, preflight.Message));
            },
            observationsToInvalidate,
            cancellationToken).ConfigureAwait(false);
        return DesktopTestingResponseMapper.MapAction(result, sessionId);
    }

    private async Task<DesktopTestingActionResponse> ExecuteSemanticInputAsync(
        string sessionId,
        string actionId,
        string operation,
        object canonicalArguments,
        Func<bool> dispatch,
        CancellationToken cancellationToken,
        params string[] observationsToInvalidate)
    {
        var result = await coordinator.ExecuteAsync(
            sessionId,
            actionId,
            canonicalArguments,
            _ => Task.FromResult(dispatch()
                ? new DesktopActionExecution(DesktopDispatchStatus.Complete)
                : new DesktopActionExecution(
                    DesktopDispatchStatus.NotStarted,
                    DesktopVerificationStatus.Failed,
                    DesktopObservationStatus.NotRequested,
                    new DesktopOperationError("InputDispatchFailed", $"The UI automation {operation} was not accepted."))),
            observationsToInvalidate,
            cancellationToken).ConfigureAwait(false);
        return DesktopTestingResponseMapper.MapAction(result, sessionId);
    }

    private static DesktopTestingActionResponse Ok(string actionId, string? sessionRef = null, string? targetRef = null) =>
        DesktopTestingResponseMapper.MapAction(new DesktopActionResult(
            DesktopTestingLimits.SchemaVersion,
            actionId,
            DesktopOperationStatus.Completed,
            DesktopDispatchStatus.Complete,
            DesktopVerificationStatus.NotRequested,
            DesktopObservationStatus.NotRequested), sessionRef, targetRef);

    private static DesktopTestingActionResponse Error(string actionId, string code, string message) =>
        DesktopTestingResponseMapper.MapAction(new DesktopActionResult(
            DesktopTestingLimits.SchemaVersion,
            actionId,
            DesktopOperationStatus.Completed,
            DesktopDispatchStatus.NotStarted,
            DesktopVerificationStatus.Inconclusive,
            DesktopObservationStatus.NotRequested,
            new DesktopOperationError(code, message)));
}
