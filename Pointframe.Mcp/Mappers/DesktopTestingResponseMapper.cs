using Pointframe.Engine;
using Pointframe.Engine.Automation.Models;
using Pointframe.Engine.Automation.Services;
using Pointframe.Mcp.Automation;

namespace Pointframe.Mcp;

public static class DesktopTestingResponseMapper
{
    private const int CompactTextLimit = 200;

    public static DesktopTestingObservationResponse MapObservation(DesktopObservationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new DesktopTestingObservationResponse(
            result.Observation.SchemaVersion,
            result.Observation.ObservationRef,
            result.Observation.TargetState.ToString(),
            result.Observation.ObservationStatus.ToString(),
            result.Observation.UiaStatus.ToString(),
            result.Observation.Process.ProcessRef,
            result.Observation.IsTruncated || result.UiAutomation?.IsTruncated == true,
            result.TopologyGeneration,
            result.Observation.PixelCapturedUtc,
            result.Observation.UiaCapturedUtc,
            result.Observation.Images.Select(image => new DesktopImageBlock(
                image.ImageRef,
                image.Width,
                image.Height,
                ToBounds(image.DesktopBoundsPixels),
                image.CapturedUtc)).ToArray(),
            result.UiAutomation?.Elements.Select(MapElement).ToArray() ?? [],
            result.UiAutomation?.ErrorCode is null
                ? null
                : new McpCaptureError(result.UiAutomation.ErrorCode, "UI automation data was not available."));
    }

    public static DesktopTestingCompactObservationResponse ToCompact(DesktopTestingObservationResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var sharedWindowRef = response.Elements
            .GroupBy(element => element.WindowRef, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .Select(group => group.Key)
            .FirstOrDefault();
        return new DesktopTestingCompactObservationResponse(
            response.SchemaVersion,
            "compact",
            response.ObservationRef,
            response.TargetState,
            response.ObservationStatus,
            response.UiaStatus,
            response.ProcessRef,
            response.IsTruncated,
            response.TopologyGeneration,
            response.PixelCapturedUtc,
            response.Images.Select(image => new DesktopCompactImageBlock(image.ImageRef, image.Width, image.Height, image.DesktopBoundsPixels)).ToArray(),
            sharedWindowRef,
            response.Elements.Select(element => new DesktopCompactElementBlock(
                element.ElementRef,
                element.Role,
                Truncate(element.Name),
                element.AutomationId,
                [element.BoundsPixels.X, element.BoundsPixels.Y, element.BoundsPixels.Width, element.BoundsPixels.Height],
                string.Equals(element.WindowRef, sharedWindowRef, StringComparison.Ordinal) ? null : element.WindowRef,
                element.IsEnabled ? null : true,
                element.ToggleState,
                element.Selection,
                Truncate(element.Text))).ToArray(),
            response.Error,
            response.Ocr);
    }
    public static DesktopTestingCheckResponse MapCheck(
        DesktopUiCheckEvaluation evaluation,
        string? sessionId = null,
        DesktopCheckSpec? spec = null,
        bool negativeControl = false,
        string? criterionId = null,
        bool reportBearing = false)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        return new DesktopTestingCheckResponse(
            DesktopTestingLimits.SchemaVersion,
            evaluation.StateAvailable
                ? evaluation.Matches ? "passed" : "failed"
                : "inconclusive",
            evaluation.StateAvailable,
            evaluation.Matches,
            evaluation.MatchCount,
            evaluation.ErrorCode is null ? null : new McpCaptureError(evaluation.ErrorCode, evaluation.Message ?? evaluation.ErrorCode),
            evaluation.ActualValue,
            RecoveryFor(evaluation.ErrorCode, sessionId, spec, negativeControl, criterionId, reportBearing));
    }

    // A negative control inverts only the verdict. Matches stays the oracle's raw answer, so a reader can
    // see that the wrong expectation was rejected rather than that the oracle said yes.
    public static DesktopTestingCheckResponse MapNegativeControl(
        DesktopUiCheckEvaluation evaluation,
        string? sessionId = null,
        DesktopCheckSpec? spec = null,
        string? criterionId = null,
        bool reportBearing = false)
    {
        // Inverting an answer read from a partial tree would turn "not seen" into a passing control.
        if (evaluation.IsTruncated)
        {
            return MapCheck(
                new DesktopUiCheckEvaluation(
                    false,
                    false,
                    evaluation.MatchCount,
                    "InspectionIncomplete",
                    "The UI Automation tree was truncated, so a negative control cannot be established.",
                    IsTruncated: true),
                sessionId,
                spec,
                negativeControl: true,
                criterionId,
                reportBearing);
        }

        var response = MapCheck(evaluation, sessionId, spec, negativeControl: true, criterionId, reportBearing);
        if (!evaluation.StateAvailable)
        {
            return response;
        }

        return evaluation.Matches
            ? response with
            {
                Verification = "failed",
                Error = new McpCaptureError("NegativeControlMatched", "The deliberately wrong expectation held, so this check cannot tell states apart."),
            }
            : response with { Verification = "passed" };
    }

    public static DesktopTestingObservationResponse WithOcr(
        DesktopTestingObservationResponse response,
        DesktopOcrObservation ocr)
    {
        return response with
        {
            Ocr = new DesktopOcrObservationResponse(
                ocr.Status,
                ocr.Text,
                ToBounds(ocr.SourceBoundsPixels),
                ocr.ErrorCode is null ? null : new McpCaptureError(ocr.ErrorCode, ocr.ErrorCode)),
        };
    }

    public static DesktopTestingActionResponse MapAction(
        DesktopActionResult result,
        string? sessionRef = null,
        string? targetRef = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new DesktopTestingActionResponse(
            result.SchemaVersion,
            result.OperationStatus.ToString(),
            result.Dispatch.ToString(),
            result.Verification.ToString(),
            result.ObservationStatus.ToString(),
            result.Error is null ? null : new McpCaptureError(result.Error.Code, result.Error.Message),
            sessionRef,
            targetRef,
            ActionId: result.ActionId,
            NextStep: RecoveryFor(result.Error?.Code, sessionRef));
    }

    // A hint with arguments is a complete, schema-valid call. Where a valid call needs input only the agent
    // has (a window ref, capture bounds), the hint is guidance-only: Arguments is null and Reason says what
    // the agent must do first.
    public static DesktopNextStep? RecoveryFor(
        string? code,
        string? sessionId = null,
        DesktopCheckSpec? spec = null,
        bool negativeControl = false,
        string? criterionId = null,
        bool reportBearing = false)
    {
        switch (code)
        {
            case "InspectionIncomplete" or "ProviderUnavailable" or "UiaUnavailable" or "InspectionTimeout":
                return CheckRecovery(code, sessionId, spec, negativeControl, criterionId, reportBearing);

            case "ProfileNotFound" or "UnknownProfile":
                return new DesktopNextStep(
                    "list_profiles",
                    "desktop_list_apps",
                    new Dictionary<string, object?> { ["actionId"] = Guid.NewGuid().ToString() },
                    Reason: "List the approved profile ids, then pass one as profileId to desktop_start_test_session.");

            case "TargetStillRunning":
                return new DesktopNextStep(
                    "close_then_restart",
                    "desktop_press_keys",
                    null,
                    Reason: "The previous process is still running. Close the app through its own UI, or press Alt+F4 with desktop_press_keys on its window (window_ref from desktop_observe_app). Then call desktop_check_ui with kind processExited until it passes, then call desktop_restart_app again with a new actionId.");

            case "StaleObservation" or "StaleObservationRef":
                return new DesktopNextStep(
                    "observe_then_retry",
                    "desktop_observe_app",
                    null,
                    Reason: $"The observation_ref is stale. Call desktop_observe_app{SessionPhrase(sessionId)} with captureBoundsPixels of the app window, then repeat the action with the new refs and a new actionId.");

            case "ElementNotFound":
                return new DesktopNextStep(
                    "observe_then_retry",
                    "desktop_observe_app",
                    null,
                    Reason: $"No element matched. Call desktop_observe_app{SessionPhrase(sessionId)} with captureBoundsPixels of the app window and includeUiAutomation true, find the element's element_ref, automationId, or role and name, then repeat the action.");
            default:
                return null;
        }
    }

    private static string SessionPhrase(string? sessionId) => sessionId is null ? string.Empty : $" with sessionId '{sessionId}'";

    private static DesktopNextStep? CheckRecovery(
        string code,
        string? sessionId,
        DesktopCheckSpec? spec,
        bool negativeControl,
        string? criterionId,
        bool reportBearing)
    {
        if (sessionId is null || spec is null)
        {
            return null;
        }

        var cause = code == "InspectionIncomplete"
            ? "The UI Automation walk stopped early"
            : "UI Automation was not available";
        if (reportBearing)
        {
            // The inconclusive check stays recorded and keeps this session's report inconclusive, so a retry in
            // the same session cannot produce passing evidence. Passing evidence needs a fresh session.
            return new DesktopNextStep(
                "fresh_session",
                "desktop_end_test_session",
                new Dictionary<string, object?> { ["sessionId"] = sessionId, ["actionId"] = Guid.NewGuid().ToString() },
                Reason: $"{cause}, and this session's report keeps the inconclusive check, so it cannot become passing. End this session, start a new one with desktop_start_test_session using the same profileId and the same acceptance criteria, repeat the actions, and run this check again{(criterionId is null ? string.Empty : $" with criterionId '{criterionId}'")}, scoped with an automationId if possible.");
        }

        var arguments = new Dictionary<string, object?> { ["sessionId"] = sessionId, ["kind"] = spec.Kind };
        AddIfSet(arguments, "automationId", spec.AutomationId);
        AddIfSet(arguments, "role", spec.Role);
        AddIfSet(arguments, "name", spec.Name);
        AddIfSet(arguments, "windowRef", spec.WindowRef);
        AddIfSet(arguments, "expected", spec.Expected);
        arguments["timeoutSeconds"] = spec.TimeoutSeconds;
        if (negativeControl)
        {
            arguments["expectFailure"] = true;
        }
        else
        {
            AddIfSet(arguments, "criterionId", criterionId);
        }

        return new DesktopNextStep(
            "diagnostic_retry",
            "desktop_check_ui",
            arguments,
            code == "InspectionIncomplete" ? 1 : 2,
            $"{cause}. This retry is diagnostic only: the inconclusive check stays in this session's report and the retry cannot change that verdict.");
    }

    private static void AddIfSet(Dictionary<string, object?> arguments, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            arguments[name] = value;
        }
    }

    private static string? Truncate(string? value)
    {
        return value is not null && value.Length > CompactTextLimit
            ? $"{value[..CompactTextLimit]}... (+{value.Length - CompactTextLimit} chars)"
            : value;
    }

    private static DesktopElementBlock MapElement(DesktopUiElementSnapshot element)
    {
        return new DesktopElementBlock(
            element.ElementRef,
            element.WindowRef,
            element.Role,
            element.Name,
            element.AutomationId,
            ToBounds(element.BoundsPixels),
            element.IsEnabled,
            element.ToggleState,
            element.Selection,
            element.IsSensitive ? null : element.Text);
    }

    private static McpPixelBounds ToBounds(PixelBounds bounds)
    {
        return new McpPixelBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }
}
