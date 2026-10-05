using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
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
    IDesktopEvidenceRecorder evidence,
    DesktopTestingPolicyLoader policyLoader,
    DesktopTestingHostOptions options)
{
    [McpServerTool(Name = "desktop_list_apps", Title = "List desktop applications", ReadOnly = true, Destructive = false, Idempotent = true, UseStructuredContent = true)]
    [Description("Acknowledges a request for desktop application candidates but never enumerates any: it always returns an empty acknowledgement. To start an application, pass a policy profile id directly to desktop_start_test_session.")]
    public Task<DesktopTestingActionResponse> ListAppsAsync(
        [Description("A UUID action identifier.")] string actionId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Ok(actionId));

    [McpServerTool(Name = "desktop_start_test_session", Title = "Start desktop test session", ReadOnly = false, Destructive = false, UseStructuredContent = true)]
    [Description("Starts one approved desktop application profile or attaches to an explicitly approved process.")]
    public async Task<DesktopTestingActionResponse> StartTestSessionAsync(
        [Description("A UUID action identifier.")] string actionId,
        [Description("The policy profile to launch. Must match a profile id declared in the desktop testing policy file.")] string profileId,
        [Description("Reserved for attaching to an already-running approved process. Attaching is not implemented by this host; leave unset to launch the profile.")] string? appRef = null,
        [Description("Optional plain-text acceptance criteria, written before the work starts, frozen and hashed as C1, C2, ... in order. When supplied, the report passes only if every criterion is covered by a passing desktop_check_ui call naming its id and at least one negative control (expectFailure) passed.")] IReadOnlyList<string>? criteria = null,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(appRef))
        {
            return Error(actionId, "AttachNotImplemented", "Attach candidates are not available from this host.");
        }

        IReadOnlyList<string> normalizedCriteria;
        try
        {
            normalizedCriteria = DesktopTestReportService.NormalizeCriteria(criteria);
        }
        catch (ArgumentException exception)
        {
            return Error(actionId, "InvalidCriteria", exception.Message);
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

        var evidenceDirectory = evidence.BeginSession(
            sessionRef,
            policy.ArtifactRoot,
            policy.EvidencePolicy switch
            {
                DesktopEvidencePolicy.All => DesktopEvidenceMode.All,
                DesktopEvidencePolicy.Failures => DesktopEvidenceMode.Failures,
                _ => DesktopEvidenceMode.None,
            });
        reports.Initialize(
            sessionRef,
            result.Session!.Target!.Process.ExecutablePath,
            result.Session.Target.Process.ExecutableSha256,
            normalizedCriteria,
            evidenceDirectory,
            Path.Combine(policy.ArtifactRoot, sessionRef));
        return Ok(actionId, sessionRef, result.Session.Target.TargetRef);
    }

    [McpServerTool(Name = "desktop_restart_app", Title = "Restart desktop application", ReadOnly = false, Destructive = false, UseStructuredContent = true)]
    [Description("Relaunches the application under test for an existing session with the same policy profile. It does not stop the application: close it through its own UI first (Close or Exit, or desktop_press_keys), or the call fails with TargetStillRunning. Use it to check that saved state survives a restart. The session id stays valid; earlier observation and element refs do not.")]
    public async Task<DesktopTestingActionResponse> RestartAppAsync(
        [Description("Session id from desktop_start_test_session.")] string sessionId,
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

        // A restart goes through the coordinator like any other action, so it lands in the signed report.
        // Checks after it only mean "survives a restart" if the report shows the restart between them.
        string? targetRef = null;
        var result = await coordinator.ExecuteAsync(
            sessionId,
            actionId,
            new { operation = "restart_app", sessionId },
            async token =>
            {
                var restart = await sessions.RestartAsync(
                    sessionId,
                    new DesktopLaunchRequest(profile.ExecutablePath, profile.Arguments, profile.WorkingDirectory),
                    token).ConfigureAwait(false);
                if (!restart.Succeeded)
                {
                    return new DesktopActionExecution(
                        DesktopDispatchStatus.NotStarted,
                        DesktopVerificationStatus.Failed,
                        DesktopObservationStatus.NotRequested,
                        new DesktopOperationError(restart.Code, restart.Message));
                }

                targetRef = restart.Session?.Target?.TargetRef;
                return new DesktopActionExecution(DesktopDispatchStatus.Complete);
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await AnnotateActionAsync(sessionId, actionId, "restart_app", result, cancellationToken).ConfigureAwait(false);
        return DesktopTestingResponseMapper.MapAction(result, sessionId, targetRef);
    }

    // No UseStructuredContent: on a method returning CallToolResult the SDK derives an output schema from
    // CallToolResult itself, with "structuredContent": true, and Claude Code then rejects the whole tool list.
    // The result builder sets StructuredContent directly, so callers still get it.
    [McpServerTool(Name = "desktop_observe_app", Title = "Observe desktop application", ReadOnly = true, Destructive = false)]
    [Description("Captures the current state of the application under test: one image block per requested rectangle, plus the UI Automation element tree when requested. It is the only source of the observation_ref and image_ref that desktop_click, desktop_drag, desktop_enter_text, and desktop_scroll require, so call it before every interaction: refs expire 30 seconds after capture or when the monitor topology changes, and are then rejected as StaleObservation.")]
    public async Task<CallToolResult> ObserveAppAsync(
        [Description("Session id from desktop_start_test_session.")] string sessionId,
        [Description("Screen rectangles to capture, in absolute desktop physical pixels, 1 through 16. Each gets its own image_ref.")] IReadOnlyList<McpPixelBounds> captureBoundsPixels,
        [Description("Whether to include the UI Automation element tree alongside the pixels. Elements carry the element_ref that desktop_invoke requires. Defaults to true.")] bool includeUiAutomation = true,
        [Description("Whether to also run OCR over the captured pixels and return the recognized text. Defaults to false.")] bool includeOcr = false,
        [Description("Which captured rectangle to run OCR over, as a zero-based index into captureBoundsPixels. Defaults to the first. Ignored unless includeOcr is true.")] int ocrImageIndex = 0,
        [Description("Whether to return the captured pixels as inline image blocks, in the same order as the images array. Defaults to true; pass false for metadata and elements only.")] bool includeImages = true,
        [Description("full (default) or compact: same refs and identity, smaller. Bounds are [x, y, width, height], nulls are omitted, an element without windowRef uses the top-level windowRef, disabled:true marks disabled elements, long names and text are truncated.")] string detail = "full",
        [Description("Longest-edge cap per image in pixels, 64 through 1600 (default 1600). Action coordinates follow the returned width and height.")] int? maxImageEdge = null,
        CancellationToken cancellationToken = default)
    {
        using var trace = Pointframe.Engine.Automation.DesktopTrace.Scope("tool desktop_observe_app");
        var compact = string.Equals(detail, "compact", StringComparison.OrdinalIgnoreCase);
        if (!compact && !string.Equals(detail, "full", StringComparison.OrdinalIgnoreCase))
        {
            return DesktopObservationResultBuilder.Build(InvalidObservationOption("InvalidDetail", "detail must be full or compact."), observation: null, includeImages: false);
        }

        if (maxImageEdge is { } edge && (edge < DesktopTestingLimits.MinImageLongestEdge || edge > DesktopTestingLimits.MaxImageLongestEdge))
        {
            return DesktopObservationResultBuilder.Build(InvalidObservationOption("InvalidImageOptions", "maxImageEdge must be 64 through 1600."), observation: null, includeImages: false);
        }
        var session = await sessions.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session?.Target is null)
        {
            // Every sibling tool reports an unknown session as a typed error. Returning null here left the
            // caller unable to distinguish "session gone" from "observed nothing".
            return DesktopObservationResultBuilder.Build(SessionNotFoundObservation(), observation: null, includeImages: false);
        }

        var result = await observations.ObserveAsync(
            new DesktopObservationRequest(
                session.Target.Process,
                captureBoundsPixels.Select(bounds => new PixelBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height)).ToArray(),
                includeUiAutomation,
                MaxImageLongestEdge: maxImageEdge),
            cancellationToken).ConfigureAwait(false);
        var response = DesktopTestingResponseMapper.MapObservation(result);
        if (includeOcr)
        {
            // The caller cannot name an image_ref here: the observation this call creates is what
            // mints them, so the previous string parameter could only ever resolve to ImageNotFound.
            // Select by index into the rectangles the caller did supply instead.
            var images = result.Observation.Images;
            response = ocrImageIndex >= 0 && ocrImageIndex < images.Count
                ? DesktopTestingResponseMapper.WithOcr(
                    response,
                    await ocr.RecognizeAsync(result, images[ocrImageIndex].ImageRef, cancellationToken).ConfigureAwait(false))
                : DesktopTestingResponseMapper.WithOcr(
                    response,
                    new DesktopOcrObservation("unavailable", null, default, "ImageIndexOutOfRange"));
        }

        return compact
            ? DesktopObservationResultBuilder.BuildCompact(DesktopTestingResponseMapper.ToCompact(response), result.Observation, includeImages)
            : DesktopObservationResultBuilder.Build(response, result.Observation, includeImages);
    }

    [McpServerTool(Name = "desktop_focus_window", Title = "Focus desktop window", ReadOnly = false, Destructive = false, UseStructuredContent = true)]
    [Description("Brings one window of the application under test to the foreground and gives it keyboard focus. Call this before desktop_press_keys when the target window may not already be focused, since key input goes to whatever currently has focus.")]
    public async Task<DesktopTestingActionResponse> FocusWindowAsync(
        [Description("Session id from desktop_start_test_session.")] string sessionId,
        [Description("A UUID action identifier.")] string actionId,
        [Description("The window reference (window_ref) of an element from a recent desktop_observe_app response.")] string windowRef,
        CancellationToken cancellationToken = default)
    {
        var session = await sessions.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session?.Target is null)
        {
            return Error(actionId, "SessionNotFound", "The desktop test session was not found.");
        }

        // The handle used to be hard-coded to zero, so focusing could never succeed. The window ref
        // now carries it, and requiring the ref to name this session's own process means a ref
        // belonging to another target cannot be used to focus an unrelated window.
        var handle = ResolveWindowHandle(windowRef, session.Target.Process.ProcessRef);
        if (handle == nint.Zero)
        {
            return Error(actionId, "WindowUnavailable", "The window reference does not name a window of this session's target.");
        }

        var target = new DesktopInputTarget(
            new DesktopWindowIdentity(windowRef, session.Target.Process.ProcessRef, handle));
        return await ExecuteInputAsync(
            sessionId,
            actionId,
            "focus_window",
            new { sessionId, windowRef },
            () => input.FocusAsync(target, session.Target.Process, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool(Name = "desktop_click", Title = "Click desktop target", ReadOnly = false, Destructive = true, UseStructuredContent = true)]
    [Description("Clicks a point inside a captured image from desktop_observe_app. Coordinates are image-local pixels relative to that image block's top-left corner, not desktop coordinates; the server maps them back to the desktop for you.")]
    public Task<DesktopTestingActionResponse> ClickAsync(
        [Description("Session id from desktop_start_test_session.")] string sessionId,
        [Description("A UUID action identifier.")] string actionId,
        [Description("observation_ref of the desktop_observe_app result that produced the image.")] string observationRef,
        [Description("image_ref of the image in that observation whose coordinates x and y use.")] string imageRef,
        [Description("Horizontal position in image-local pixels from the left edge; inside the image.")] int x,
        [Description("Vertical position in image-local pixels from the top edge; inside the image.")] int y,
        [Description("Number of clicks: 1 for a single click, 2 for a double click. Values above 2 are rejected. Defaults to 1.")] int count = 1,
        [Description("Whether to click with the right mouse button instead of the left. Defaults to false.")] bool rightButton = false,
        CancellationToken cancellationToken = default) =>
        ClickCoreAsync(sessionId, actionId, observationRef, imageRef, x, y, count, rightButton, cancellationToken);

    [McpServerTool(Name = "desktop_press_keys", Title = "Press desktop keys", ReadOnly = false, Destructive = true, UseStructuredContent = true)]
    [Description("Presses a chord of keys, given as Windows virtual-key codes, against whatever currently has keyboard focus. A physical press must name the observed target with windowRef (a window_ref from desktop_observe_app) and/or observationRef; that window must still exist and be foreground, so call desktop_focus_window first unless it already is. The observationRef is consumed. Keys are pressed in the given order and released in reverse, so pass modifiers first (for example [0x11, 0x43] for Ctrl+C).")]
    public Task<DesktopTestingActionResponse> PressKeysAsync(
        [Description("Session id from desktop_start_test_session.")] string sessionId,
        [Description("A UUID action identifier.")] string actionId,
        [Description("One to four Windows virtual-key codes to press together, modifiers first (for example 0x11 Ctrl, 0x10 Shift, 0x12 Alt). More than four keys is rejected.")] IReadOnlyList<ushort> virtualKeys,
        [Description("Set only when the chord is a system-wide hotkey rather than input to the focused window. Must name a hotkey the active policy profile approves, or the action is rejected.")] string? globalHotkeyId = null,
        [Description("observation_ref of the desktop_observe_app result the key press follows. Required for a physical press unless windowRef is given; it must not be stale, and a windowRef must belong to it. It is consumed by the action. Ignored for a global hotkey.")] string? observationRef = null,
        [Description("The window reference (window_ref) of the observed window that must receive the keys. It must be the foreground window. When omitted, the observation's process must be foreground. Ignored for a global hotkey.")] string? windowRef = null,
        CancellationToken cancellationToken = default) =>
        PressKeysCoreAsync(sessionId, actionId, virtualKeys, globalHotkeyId, observationRef, windowRef, cancellationToken);

    [McpServerTool(Name = "desktop_drag", Title = "Drag desktop target", ReadOnly = false, Destructive = true, UseStructuredContent = true)]
    [Description("Presses the left mouse button at the first point, moves through the remaining points in order, and releases at the last. Requires at least two points. Coordinates are image-local pixels within the given observation image, as for desktop_click.")]
    public Task<DesktopTestingActionResponse> DragAsync(
        [Description("Session id from desktop_start_test_session.")] string sessionId,
        [Description("A UUID action identifier.")] string actionId,
        [Description("observation_ref of the desktop_observe_app result that produced the image.")] string observationRef,
        [Description("image_ref of the image in that observation whose coordinates the points use.")] string imageRef,
        [Description("The drag path, in order, as image-local pixel positions: at least 2 and at most 128. Only x and y are used; width and height are ignored and may be zero.")] IReadOnlyList<McpPixelBounds> points,
        [Description("How long the whole drag should take, in milliseconds, from 50 through 5000. Longer drags are more reliable with animated or drag-threshold-sensitive UI. Defaults to 250.")] int durationMilliseconds = 250,
        CancellationToken cancellationToken = default) =>
        DragCoreAsync(sessionId, actionId, observationRef, imageRef, points, durationMilliseconds, cancellationToken);

    [McpServerTool(Name = "desktop_enter_text", Title = "Enter desktop text", ReadOnly = false, Destructive = true, UseStructuredContent = true)]
    [Description("Clicks a point to place the caret and then types text into it. Prefer semantic entry with an element_ref where the field exposes it, since that sets the value directly instead of relying on synthesized keystrokes.")]
    public Task<DesktopTestingActionResponse> EnterTextAsync(
        [Description("Session id from desktop_start_test_session.")] string sessionId,
        [Description("A UUID action identifier.")] string actionId,
        [Description("observation_ref of the desktop_observe_app result that produced the image.")] string observationRef,
        [Description("image_ref of the image in that observation whose coordinates x and y use.")] string imageRef,
        [Description("Horizontal position of the text field in image-local pixels.")] int x,
        [Description("Vertical position of the text field in image-local pixels.")] int y,
        [Description("The literal text to enter. At most 4096 characters.")] string text,
        [Description("Set the field's value through UI Automation instead of typing keystrokes. Requires elementRef. Defaults to false.")] bool semanticValue = false,
        [Description("The element_ref of the target field from desktop_observe_app. Required when semanticValue is true.")] string? elementRef = null,
        CancellationToken cancellationToken = default) =>
        EnterTextCoreAsync(sessionId, actionId, observationRef, imageRef, x, y, text, semanticValue, elementRef, cancellationToken);

    [McpServerTool(Name = "desktop_invoke", Title = "Invoke desktop element", ReadOnly = false, Destructive = true, UseStructuredContent = true)]
    [Description("Activates a UI Automation element through its invoke pattern, without moving the mouse or synthesizing input. Prefer it over desktop_click for buttons and menu items that expose an element_ref: it does not depend on visibility, occlusion, or pixel mapping.")]
    public async Task<DesktopTestingActionResponse> InvokeAsync(
        [Description("Session id from desktop_start_test_session.")] string sessionId,
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

    [McpServerTool(Name = "desktop_check_ui", Title = "Check desktop UI", ReadOnly = true, Destructive = false, UseStructuredContent = true)]
    [Description("Waits until a condition holds over the application's UI Automation state, or the timeout elapses; use it to synchronize with the UI after an action instead of guessing at delays. Returns verification 'passed', 'failed', or 'inconclusive' (state unreadable), plus actualValue when one element's state was read. Every evaluated check is recorded in the session report and a failed check fails the report, so explore with desktop_observe_app instead.")]
    public async Task<DesktopTestingCheckResponse> CheckUiAsync(
        [Description("Session id from desktop_start_test_session.")] string sessionId,
        [Description("What to check: exists/absent find elements; enabled checks true/false; toggleEquals checks checked or unchecked state; selectionEquals checks selected state; textEquals checks exact text; windowExists/windowAbsent check a window; processExited checks process liveness.")] string kind,
        [Description("The AutomationId of the element to check. Supply this or both role and name.")] string? automationId = null,
        [Description("The control type of the element to check, such as Button or Edit. Use with name.")] string? role = null,
        [Description("The accessible name of the element to check. Use with role.")] string? name = null,
        [Description("Optional window_ref from desktop_observe_app, to scope the check to one window. Required for windowExists and windowAbsent.")] string? windowRef = null,
        [Description("The expected value: enabled accepts true/false; toggleEquals accepts true/checked/on or false/unchecked/off (reported as On/Off), or indeterminate; selectionEquals accepts selected/notSelected; textEquals accepts exact text.")] string? expected = null,
        [Description("How long to wait for the condition before giving up, in seconds. At most 30.")] int timeoutSeconds = DesktopTestingLimits.DefaultUiCheckTimeoutSeconds,
        [Description("Optional acceptance criterion id (C1, C2, ...) from desktop_start_test_session that this check provides evidence for. An id the session did not declare is rejected without running the check.")] string? criterionId = null,
        [Description("Negative control: a deliberately wrong expectation that passes only when the condition does not hold, proving the check can tell states apart. A session with criteria needs one passing negative control. Cannot be combined with criterionId.")] bool expectFailure = false,
        CancellationToken cancellationToken = default)
    {
        var session = await sessions.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session?.Target is null)
        {
            return DesktopTestingResponseMapper.MapCheck(
                new DesktopUiCheckEvaluation(false, false, 0, "SessionNotFound"));
        }

        if (expectFailure && criterionId is not null)
        {
            return DesktopTestingResponseMapper.MapCheck(
                new DesktopUiCheckEvaluation(false, false, 0, "InvalidCondition", "A negative control cannot cover a criterion."));
        }

        if (criterionId is not null && !reports.HasCriterion(sessionId, criterionId))
        {
            return DesktopTestingResponseMapper.MapCheck(
                new DesktopUiCheckEvaluation(false, false, 0, "UnknownCriterion", $"The session declared no criterion '{criterionId}'."));
        }

        var spec = new DesktopCheckSpec(kind, automationId, role, name, windowRef, expected, timeoutSeconds);
        var (response, _) = await EvaluateAndRecordCheckAsync(
            sessionId, session.Target.Process, spec, criterionId, expectFailure, descriptionPrefix: null, cancellationToken).ConfigureAwait(false);
        return response;
    }

    private async Task<(DesktopTestingCheckResponse Response, DesktopVerificationStatus? Verdict)> EvaluateAndRecordCheckAsync(
        string sessionId,
        DesktopProcessIdentity process,
        DesktopCheckSpec spec,
        string? criterionId,
        bool expectFailure,
        string? descriptionPrefix,
        CancellationToken cancellationToken)
    {
        var request = new McpUiCheckRequest(spec.Kind, spec.AutomationId, spec.Role, spec.Name, spec.WindowRef, spec.Expected, process.ProcessRef);
        DesktopUiCheckCondition condition;
        try
        {
            condition = BuildCondition(request);
        }
        catch (ArgumentException exception)
        {
            return (DesktopTestingResponseMapper.MapCheck(
                new DesktopUiCheckEvaluation(false, false, 0, "InvalidCondition", exception.Message)), null);
        }

        var evaluation = await checks.CheckAsync(
            process,
            condition,
            TimeSpan.FromSeconds(spec.TimeoutSeconds),
            cancellationToken).ConfigureAwait(false);
        var response = expectFailure
            ? DesktopTestingResponseMapper.MapNegativeControl(evaluation)
            : DesktopTestingResponseMapper.MapCheck(evaluation);

        // The report is the record of what the server itself observed. Only checks the server evaluated
        // land here; requests rejected before evaluation say nothing about the application.
        var verdict = response.Verification switch
        {
            "passed" => DesktopVerificationStatus.Passed,
            "failed" => DesktopVerificationStatus.Failed,
            _ => DesktopVerificationStatus.Inconclusive,
        };
        var checkEvidence = evidence.ShouldCapture(sessionId, verdict != DesktopVerificationStatus.Passed)
            ? evidence.Capture(sessionId, process, "check")
            : null;
        reports.RecordCheck(sessionId, new DesktopTestCheckReport(
            descriptionPrefix + DescribeCheck(request, expectFailure),
            verdict,
            External: false,
            OracleType: "server-uia",
            Message: response.Error?.Message ?? (response.Verification == "failed" && response.ActualValue is not null
                ? $"Expected '{spec.Expected}', but found '{response.ActualValue}'."
                : null),
            RecordedUtc: DateTimeOffset.UtcNow,
            CriterionId: criterionId,
            NegativeControl: expectFailure,
            Evidence: checkEvidence,
            Spec: spec));
        return (response, verdict);
    }

    [McpServerTool(Name = "desktop_replay_checks", Title = "Replay recorded checks", ReadOnly = false, Destructive = false, UseStructuredContent = true)]
    [Description("Re-runs the checks of a signed report (a report.json under the policy's artifact root whose proof verifies and whose executable hash equals this session's) against a fresh session and compares verdicts per check and per criterion. Use it to confirm results that should survive a restart. Checks scoped to a window_ref are skipped, since window refs belong to one session. Replayed checks are also recorded in this session's report. Status is 'matched' when every replayed verdict equals the original, otherwise 'differs'.")]
    public async Task<DesktopReplayResponse> ReplayChecksAsync(
        [Description("The id of a fresh session from desktop_start_test_session, running the same executable as the report.")] string sessionId,
        [Description("Absolute path to the report.json of a proof bundle, under the policy's artifact root.")] string reportPath,
        CancellationToken cancellationToken = default)
    {
        var session = await sessions.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session?.Target is null)
        {
            return ReplayError("SessionNotFound", "The desktop test session was not found.");
        }

        // The server reads this file on the caller's behalf, so it may only read inside the approved root.
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(LoadPolicy().ArtifactRoot)) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(reportPath);
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return ReplayError("ReportOutsideArtifactRoot", "The report must be under the policy's artifact root.");
        }

        if (!File.Exists(fullPath))
        {
            return ReplayError("ReportNotFound", $"No report at {fullPath}.");
        }

        var json = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
        var evidenceDirectory = Path.Combine(Path.GetDirectoryName(fullPath)!, DesktopProofBundle.EvidenceFolderName);
        var verification = DesktopProofService.Verify(json, Directory.Exists(evidenceDirectory) ? evidenceDirectory : null);
        if (!verification.IsValid)
        {
            return ReplayError("ProofInvalid", string.Join(" ", verification.Problems));
        }

        var original = JsonSerializer.Deserialize<DesktopTestReport>(json, DesktopProofService.CanonicalJson)!;
        if (!string.Equals(original.ExecutableSha256, session.Target.Process.ExecutableSha256, StringComparison.OrdinalIgnoreCase))
        {
            return ReplayError("ExecutableMismatch", "The session runs a different executable from the one the report was signed for.");
        }

        var replayedChecks = new List<DesktopReplayCheck>();
        var replayedRecords = new List<DesktopTestCheckReport>();
        for (var index = 0; index < original.Checks.Count; index++)
        {
            var check = original.Checks[index];
            var originalVerdict = check.Verdict.ToString().ToLowerInvariant();
            var skipped = check.Spec switch
            {
                null => "NoRecordedCondition",
                { WindowRef: not null } => "WindowRefNotReplayable",
                _ => null,
            };
            if (skipped is not null)
            {
                replayedChecks.Add(new DesktopReplayCheck(index, check.Description, originalVerdict, null, skipped));
                continue;
            }

            var (_, verdict) = await EvaluateAndRecordCheckAsync(
                sessionId, session.Target.Process, check.Spec!, criterionId: null, check.NegativeControl, "replay: ", cancellationToken).ConfigureAwait(false);
            var replayed = verdict ?? DesktopVerificationStatus.Inconclusive;
            replayedChecks.Add(new DesktopReplayCheck(index, check.Description, originalVerdict, replayed.ToString().ToLowerInvariant()));
            replayedRecords.Add(check with { Verdict = replayed });
        }

        var criteria = original.Criteria
            .Select(criterion => new DesktopReplayCriterion(
                criterion.Id,
                criterion.Verdict,
                DesktopTestReportService.ComputeCriterionVerdict(criterion.Id, replayedRecords)))
            .ToArray();
        var matched = criteria.All(criterion => criterion.Original == criterion.Replayed)
            && replayedChecks.All(check => check.Skipped is not null || check.Original == check.Replayed);
        return new DesktopReplayResponse(
            DesktopTestingLimits.SchemaVersion,
            matched ? "matched" : "differs",
            original.SessionRef,
            verification.KeyId,
            criteria,
            replayedChecks);
    }

    private static DesktopReplayResponse ReplayError(string code, string message) =>
        new(DesktopTestingLimits.SchemaVersion, "error", null, null, [], [], new McpCaptureError(code, message));

    internal static string DescribeCheck(McpUiCheckRequest request, bool expectFailure = false)
    {
        var parts = new List<string> { expectFailure ? $"not {request.Kind}" : request.Kind };
        if (!string.IsNullOrWhiteSpace(request.AutomationId))
        {
            parts.Add($"automationId={request.AutomationId}");
        }

        if (!string.IsNullOrWhiteSpace(request.Role))
        {
            parts.Add($"role={request.Role}");
        }

        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            parts.Add($"name={request.Name}");
        }

        if (!string.IsNullOrWhiteSpace(request.WindowRef))
        {
            parts.Add($"window={request.WindowRef}");
        }

        if (request.Expected is not null)
        {
            parts.Add($"expected={request.Expected}");
        }

        return string.Join(' ', parts);
    }

    internal static nint ResolveWindowHandle(string windowRef, string processRef)
    {
        if (string.IsNullOrWhiteSpace(windowRef) || string.IsNullOrWhiteSpace(processRef))
        {
            return nint.Zero;
        }

        var prefix = $"window-{processRef}-";
        if (!windowRef.StartsWith(prefix, StringComparison.Ordinal))
        {
            return nint.Zero;
        }

        return long.TryParse(
            windowRef[prefix.Length..],
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value) && value != 0
            ? new nint(value)
            : nint.Zero;
    }

    internal static DesktopUiCheckCondition BuildCondition(McpUiCheckRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        DesktopLocator Locator()
        {
            var locator = string.IsNullOrWhiteSpace(request.AutomationId)
                ? new DesktopLocator(DesktopLocatorKind.RoleAndName, Role: request.Role, Name: request.Name, WindowRef: request.WindowRef)
                : new DesktopLocator(DesktopLocatorKind.AutomationId, AutomationId: request.AutomationId, WindowRef: request.WindowRef);
            locator.Validate();
            return locator;
        }

        string Expected() => request.Expected
            ?? throw new ArgumentException($"The '{request.Kind}' condition requires an expected value.", nameof(request));

        bool ExpectedBoolean() => request.Expected switch
        {
            null => true,
            _ when bool.TryParse(request.Expected, out var value) => value,
            _ => throw new ArgumentException($"The '{request.Kind}' condition requires expected to be 'true' or 'false'.", nameof(request)),
        };

        string ExpectedToggle() => request.Expected?.Trim().ToLowerInvariant() switch
        {
            "true" or "checked" or "on" => "On",
            "false" or "unchecked" or "off" => "Off",
            "indeterminate" => "Indeterminate",
            _ => throw new ArgumentException("The 'toggleEquals' condition expects true/checked/on, false/unchecked/off, or indeterminate.", nameof(request)),
        };

        string ExpectedSelection() => request.Expected?.Trim().ToLowerInvariant() switch
        {
            "selected" => "Selected",
            "notselected" or "not selected" => "NotSelected",
            _ => throw new ArgumentException("The 'selectionEquals' condition expects selected or notSelected.", nameof(request)),
        };

        string Window() => string.IsNullOrWhiteSpace(request.WindowRef)
            ? throw new ArgumentException($"The '{request.Kind}' condition requires a window reference.", nameof(request))
            : request.WindowRef;

        return request.Kind?.ToLowerInvariant() switch
        {
            "exists" => new DesktopUiCheckCondition.Exists(Locator()),
            "absent" => new DesktopUiCheckCondition.Absent(Locator()),
            "enabled" => new DesktopUiCheckCondition.Enabled(Locator(), ExpectedBoolean()),
            "toggleequals" => new DesktopUiCheckCondition.ToggleEquals(Locator(), ExpectedToggle()),
            "selectionequals" => new DesktopUiCheckCondition.SelectionEquals(Locator(), ExpectedSelection()),
            "textequals" => new DesktopUiCheckCondition.TextEquals(Locator(), Expected()),
            "windowexists" => new DesktopUiCheckCondition.WindowExists(Window()),
            "windowabsent" => new DesktopUiCheckCondition.WindowAbsent(Window()),
            "processexited" => new DesktopUiCheckCondition.ProcessExited(request.ProcessRef ?? string.Empty),
            _ => throw new ArgumentException($"Unknown condition kind '{request.Kind}'.", nameof(request)),
        };
    }

    [McpServerTool(Name = "desktop_scroll", Title = "Scroll desktop target", ReadOnly = false, Destructive = true, UseStructuredContent = true)]
    [Description("Scrolls the mouse wheel over a point inside a captured image from desktop_observe_app. Coordinates are image-local pixels, as for desktop_click.")]
    public Task<DesktopTestingActionResponse> ScrollAsync(
        [Description("Session id from desktop_start_test_session.")] string sessionId,
        [Description("A UUID action identifier.")] string actionId,
        [Description("observation_ref of the desktop_observe_app result that produced the image.")] string observationRef,
        [Description("image_ref of the image in that observation whose coordinates x and y use.")] string imageRef,
        [Description("Horizontal position to scroll over, in image-local pixels.")] int x,
        [Description("Vertical position to scroll over, in image-local pixels.")] int y,
        [Description("Number of wheel detents (notches), from -10 through 10. Positive scrolls up, negative scrolls down.")] int detents,
        CancellationToken cancellationToken = default) =>
        ScrollCoreAsync(sessionId, actionId, observationRef, imageRef, x, y, detents, cancellationToken);

    [McpServerTool(Name = "desktop_get_action_result", Title = "Get desktop action result", ReadOnly = true, Destructive = false, Idempotent = true, UseStructuredContent = true)]
    [Description("Returns the recorded outcome of a previously dispatched action by its action id. Use this to resolve an action whose result was uncertain; actions are never replayed, so re-issuing the original call is not a safe alternative.")]
    public Task<DesktopTestingActionResponse> GetActionResultAsync(
        [Description("The UUID action identifier that was passed to the original action.")] string actionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(DesktopTestingResponseMapper.MapAction(coordinator.GetResult(actionId)));
    }

    [McpServerTool(Name = "desktop_get_test_report", Title = "Get desktop test report", ReadOnly = false, Destructive = false, Idempotent = true, UseStructuredContent = true)]
    [Description("Finalizes and returns the signed report for a session: executable and hash, every action and check with its evidence, criteria verdicts, overall verdict, and proof. Also writes a proof bundle to sessionDirectory: report.json, the evidence folder, and an index.html timeline for a person. Call before desktop_end_test_session if the report is needed.")]
    public async Task<DesktopTestReport> GetTestReportAsync(
        [Description("Session id from desktop_start_test_session.")] string sessionId,
        [Description("full (default) or compact: drops per-item evidence, check conditions and the proof from this response only; verify from sessionDirectory/report.json, which is always the full signed report.")] string detail = "full",
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(detail, "full", StringComparison.OrdinalIgnoreCase) && !string.Equals(detail, "compact", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("detail must be full or compact.", nameof(detail));
        }

        var report = await coordinator.FinalizeAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (report.SessionDirectory is not null)
        {
            await DesktopProofBundle.WriteAsync(report, report.SessionDirectory, cancellationToken).ConfigureAwait(false);
        }

        return string.Equals(detail, "compact", StringComparison.OrdinalIgnoreCase) ? DesktopTestReportCompaction.Compact(report) : report;
    }

    [McpServerTool(Name = "desktop_end_test_session", Title = "End desktop test session", ReadOnly = false, Destructive = true, UseStructuredContent = true)]
    [Description("Ends the session and stops the application it launched. Observation, image, and element references from the session become invalid. Always call this when finished, so the target process is not left running.")]
    public async Task<DesktopTestingActionResponse> EndTestSessionAsync(
        [Description("Session id from desktop_start_test_session.")] string sessionId,
        [Description("A UUID action identifier.")] string actionId,
        CancellationToken cancellationToken = default)
    {
        var result = await sessions.EndAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? Ok(actionId) : Error(actionId, result.Code, result.Message);
    }

    private static DesktopTestingObservationResponse InvalidObservationOption(string code, string message)
    {
        return SessionNotFoundObservation() with { Error = new McpCaptureError(code, message) };
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
        string? observationRef,
        string? windowRef,
        CancellationToken cancellationToken)
    {
        var session = await sessions.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session?.Target is null)
        {
            return Error(actionId, "SessionNotFound", "The desktop test session was not found.");
        }

        var isGlobalHotkey = !string.IsNullOrWhiteSpace(globalHotkeyId);
        if (isGlobalHotkey)
        {
            var profile = LoadPolicy().Profiles.SingleOrDefault(item => item.Id == session.ProfileId);
            if (profile is null || !profile.AllowedGlobalHotkeys.ContainsKey(globalHotkeyId!))
            {
                return Error(actionId, "GlobalHotkeyNotApproved", "The global hotkey is not approved for the active profile.");
            }
        }

        DesktopInputTarget? target = null;
        string[] consumed = [];
        if (!isGlobalHotkey)
        {
            try
            {
                var resolved = ResolvePressKeysTarget(session.Target.Process.ProcessRef, observationRef, windowRef);
                if (resolved.Error is not null)
                {
                    return Error(actionId, resolved.Error.Value.Code, resolved.Error.Value.Message);
                }

                target = resolved.Target;
                consumed = resolved.ObservationRef is null ? [] : [resolved.ObservationRef];
            }
            catch (DesktopOperationException exception)
            {
                return Error(actionId, exception.Code, exception.Message);
            }
        }

        var method = isGlobalHotkey ? DesktopInputMethod.GlobalHotkey : DesktopInputMethod.Physical;
        return await ExecuteInputAsync(
            sessionId,
            actionId,
            "press_keys",
            new { sessionId, virtualKeys, globalHotkeyId, observationRef, windowRef },
            () => input.PressKeysAsync(
                new DesktopKeyPressRequest(target, virtualKeys, method, globalHotkeyId),
                session.Target.Process,
                cancellationToken),
            cancellationToken,
            consumed).ConfigureAwait(false);
    }

    private (DesktopInputTarget? Target, string? ObservationRef, (string Code, string Message)? Error) ResolvePressKeysTarget(
        string processRef,
        string? observationRef,
        string? windowRef)
    {
        var hasObservation = !string.IsNullOrWhiteSpace(observationRef);
        var hasWindow = !string.IsNullOrWhiteSpace(windowRef);
        if (!hasObservation && !hasWindow)
        {
            return (null, null, ("TargetRequired", "A physical key press needs a windowRef and/or observationRef from desktop_observe_app."));
        }

        var observation = hasObservation ? observations.Resolve(observationRef!) : null;
        if (!hasWindow)
        {
            var bounds = observation!.Observation.Images.FirstOrDefault()?.DesktopBoundsPixels;
            return bounds is null
                ? (null, null, ("TargetUnavailable", "The observation has no image to anchor the key press to."))
                : (new DesktopInputTarget(BoundsPixels: bounds), observation.Observation.ObservationRef, null);
        }

        if (observation?.UiAutomation is { } snapshot &&
            !snapshot.Elements.Any(element => string.Equals(element.WindowRef, windowRef, StringComparison.Ordinal)))
        {
            return (null, null, ("WindowUnavailable", "The window reference is not part of the given observation."));
        }

        var handle = ResolveWindowHandle(windowRef!, processRef);
        if (handle == nint.Zero)
        {
            return (null, null, ("WindowUnavailable", "The window reference does not name a window of this session's target."));
        }

        return (
            new DesktopInputTarget(new DesktopWindowIdentity(windowRef!, processRef, handle)),
            observation?.Observation.ObservationRef,
            null);
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
        await AnnotateActionAsync(sessionId, actionId, operation, result, cancellationToken).ConfigureAwait(false);
        return DesktopTestingResponseMapper.MapAction(result, sessionId);
    }

    // The screenshot is taken immediately after dispatch, so it shows what the screen looked like when the
    // action returned, not necessarily the settled result; checks wait for their condition and show that.
    private async Task AnnotateActionAsync(
        string sessionId,
        string actionId,
        string operation,
        DesktopActionResult result,
        CancellationToken cancellationToken)
    {
        if (!reports.IsActionUnannotated(sessionId, actionId))
        {
            return;
        }

        // An action rejected before dispatch is still named in the report, but the screen it would show
        // was not affected by it, so it gets no screenshot.
        DesktopTestEvidence? actionEvidence = null;
        var isFailure = result.Verification == DesktopVerificationStatus.Failed
            || result.Dispatch is DesktopDispatchStatus.Partial or DesktopDispatchStatus.Unknown;
        if (result.Dispatch != DesktopDispatchStatus.NotStarted && evidence.ShouldCapture(sessionId, isFailure))
        {
            var session = await sessions.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (session?.Target is not null)
            {
                actionEvidence = evidence.Capture(sessionId, session.Target.Process, "action");
            }
        }

        reports.AnnotateAction(sessionId, actionId, operation, actionEvidence);
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
        await AnnotateActionAsync(sessionId, actionId, operation, result, cancellationToken).ConfigureAwait(false);
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
