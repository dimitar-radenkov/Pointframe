using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Pointframe.Telemetry;

namespace Pointframe.Mcp;

internal static class McpTelemetryFilter
{
    private static readonly HashSet<string> _deniedErrorCodes = new(StringComparer.Ordinal)
    {
        "GlobalHotkeyNotApproved",
        "ProfileNotFound",
    };

    private const string VerificationResultErrorCode = "NegativeControlMatched";

    internal static McpRequestFilter<CallToolRequestParams, CallToolResult> Create(IOperationTelemetry telemetry)
    {
        return next => async (context, cancellationToken) =>
        {
            var started = Stopwatch.GetTimestamp();
            var outcome = TelemetryOutcome.Error;
            try
            {
                var result = await next(context, cancellationToken);
                outcome = Classify(result);
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                outcome = TelemetryOutcome.Cancelled;
                throw;
            }
            finally
            {
                Track(telemetry, context, outcome, Stopwatch.GetElapsedTime(started));
            }
        };
    }

    internal static TelemetryOutcome Classify(CallToolResult result)
    {
        var code = ErrorCode(result.StructuredContent);
        if (code is not null && _deniedErrorCodes.Contains(code))
        {
            return TelemetryOutcome.Denied;
        }

        if (result.IsError == true)
        {
            return TelemetryOutcome.Error;
        }

        // Typed tools return their failure in the structured content and leave IsError unset, so the
        // wire result stays unchanged and the failure is read from the content instead.
        return HasTypedFailure(result.StructuredContent) ? TelemetryOutcome.Error : TelemetryOutcome.Success;
    }

    private static void Track(IOperationTelemetry telemetry, RequestContext<CallToolRequestParams> context, TelemetryOutcome outcome, TimeSpan duration)
    {
        try
        {
            telemetry.Track(
                TelemetryAllowlist.OperationName(TelemetryHost.Mcp, context.Params?.Name),
                outcome,
                duration,
                context.Server.ClientInfo?.Name);
        }
        catch (Exception)
        {
            // Telemetry must never change the result of a tool call.
        }
    }

    // A typed response failed the operation when it carries a top-level error or reports success=false.
    // A desktop check that ran and found the application not in the expected state is a verification
    // result, not a tool failure: "failed" and "inconclusive" without an error, and a negative control
    // that matched (NegativeControlMatched), count as Success.
    private static bool HasTypedFailure(JsonElement? structuredContent)
    {
        if (structuredContent is not { ValueKind: JsonValueKind.Object } content)
        {
            return false;
        }

        foreach (var property in content.EnumerateObject())
        {
            if (property.Name.Equals("success", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.False)
            {
                return true;
            }

            if (property.Name.Equals("error", StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.String
                && !string.Equals(ErrorCode(structuredContent), VerificationResultErrorCode, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string? ErrorCode(JsonElement? structuredContent)
    {
        if (structuredContent is not { ValueKind: JsonValueKind.Object } content)
        {
            return null;
        }

        foreach (var property in content.EnumerateObject())
        {
            if (property.Name.Equals("error", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var errorProperty in property.Value.EnumerateObject())
                {
                    if (errorProperty.Name.Equals("code", StringComparison.OrdinalIgnoreCase) && errorProperty.Value.ValueKind == JsonValueKind.String)
                    {
                        return errorProperty.Value.GetString();
                    }
                }
            }
        }

        return null;
    }
}
