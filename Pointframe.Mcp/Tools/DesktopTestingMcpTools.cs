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
    [McpServerTool(Title = "List desktop applications", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("Lists policy-eligible desktop application candidates without changing focus.")]
    public Task<DesktopTestingActionResponse> ListAppsAsync(
        [Description("A UUID action identifier.")] string actionId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Ok(actionId));

    [McpServerTool(Title = "Start desktop test session", UseStructuredContent = true)]
    [Description("Starts one approved desktop application profile or attaches to an explicitly approved process.")]
    public async Task<DesktopTestingActionResponse> StartTestSessionAsync(
        [Description("A UUID action identifier.")] string actionId,
        [Description("The policy profile to launch.")] string profileId,
        string? appRef = null,
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

    [McpServerTool(Title = "Restart desktop application", UseStructuredContent = true)]
    public async Task<DesktopTestingActionResponse> RestartAppAsync(
        string sessionId,
        string actionId,
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

    [McpServerTool(Title = "Observe desktop application", ReadOnly = true, UseStructuredContent = true)]
    public async Task<DesktopTestingObservationResponse?> ObserveAppAsync(
        string sessionId,
        IReadOnlyList<McpPixelBounds> captureBoundsPixels,
        bool includeUiAutomation = true,
        string? ocrImageRef = null,
        CancellationToken cancellationToken = default)
    {
        var session = await sessions.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session?.Target is null)
        {
            return null;
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

    [McpServerTool(Title = "Focus desktop window", UseStructuredContent = true)]
    public async Task<DesktopTestingActionResponse> FocusWindowAsync(
        string sessionId,
        string actionId,
        string windowRef,
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

    [McpServerTool(Title = "Click desktop target", UseStructuredContent = true)]
    public Task<DesktopTestingActionResponse> ClickAsync(string sessionId, string actionId, string observationRef, string imageRef, int x, int y, int count = 1, bool rightButton = false, CancellationToken cancellationToken = default) =>
        ClickCoreAsync(sessionId, actionId, observationRef, imageRef, x, y, count, rightButton, cancellationToken);

    [McpServerTool(Title = "Press desktop keys", UseStructuredContent = true)]
    public Task<DesktopTestingActionResponse> PressKeysAsync(string sessionId, string actionId, IReadOnlyList<ushort> virtualKeys, string? globalHotkeyId = null, CancellationToken cancellationToken = default) =>
        PressKeysCoreAsync(sessionId, actionId, virtualKeys, globalHotkeyId, cancellationToken);

    [McpServerTool(Title = "Drag desktop target", UseStructuredContent = true)]
    public Task<DesktopTestingActionResponse> DragAsync(string sessionId, string actionId, string observationRef, string imageRef, IReadOnlyList<McpPixelBounds> points, int durationMilliseconds = 250, CancellationToken cancellationToken = default) =>
        DragCoreAsync(sessionId, actionId, observationRef, imageRef, points, durationMilliseconds, cancellationToken);

    [McpServerTool(Title = "Enter desktop text", UseStructuredContent = true)]
    public Task<DesktopTestingActionResponse> EnterTextAsync(string sessionId, string actionId, string observationRef, string imageRef, int x, int y, string text, bool semanticValue = false, string? elementRef = null, CancellationToken cancellationToken = default) =>
        EnterTextCoreAsync(sessionId, actionId, observationRef, imageRef, x, y, text, semanticValue, elementRef, cancellationToken);

    [McpServerTool(Title = "Invoke desktop element", UseStructuredContent = true)]
    public async Task<DesktopTestingActionResponse> InvokeAsync(
        string sessionId,
        string actionId,
        string elementRef,
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

    [McpServerTool(Title = "Check desktop UI", ReadOnly = true, UseStructuredContent = true)]
    public async Task<DesktopTestingCheckResponse> CheckUiAsync(
        string sessionId,
        DesktopUiCheckCondition condition,
        int timeoutSeconds = DesktopTestingLimits.DefaultUiCheckTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        _ = sessionId;
        var evaluation = await checks.CheckAsync(condition, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken).ConfigureAwait(false);
        return DesktopTestingResponseMapper.MapCheck(evaluation);
    }

    [McpServerTool(Title = "Scroll desktop target", UseStructuredContent = true)]
    public Task<DesktopTestingActionResponse> ScrollAsync(string sessionId, string actionId, string observationRef, string imageRef, int x, int y, int detents, CancellationToken cancellationToken = default) =>
        ScrollCoreAsync(sessionId, actionId, observationRef, imageRef, x, y, detents, cancellationToken);

    [McpServerTool(Title = "Get desktop action result", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    public Task<DesktopTestingActionResponse> GetActionResultAsync(string actionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(DesktopTestingResponseMapper.MapAction(coordinator.GetResult(actionId)));
    }

    [McpServerTool(Title = "Get desktop test report", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    public async Task<DesktopTestReport> GetTestReportAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        return await coordinator.FinalizeAsync(sessionId, cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool(Title = "End desktop test session", UseStructuredContent = true)]
    public async Task<DesktopTestingActionResponse> EndTestSessionAsync(string sessionId, string actionId, CancellationToken cancellationToken = default)
    {
        var result = await sessions.EndAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? Ok(actionId) : Error(actionId, result.Code, result.Message);
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
