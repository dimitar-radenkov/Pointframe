using Pointframe.Engine;
using Pointframe.Engine.Automation.Models;
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
    public static DesktopTestingCheckResponse MapCheck(DesktopUiCheckEvaluation evaluation)
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
            evaluation.ActualValue);
    }

    // A negative control inverts only the verdict. Matches stays the oracle's raw answer, so a reader can
    // see that the wrong expectation was rejected rather than that the oracle said yes.
    public static DesktopTestingCheckResponse MapNegativeControl(DesktopUiCheckEvaluation evaluation)
    {
        var response = MapCheck(evaluation);
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
            targetRef);
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
