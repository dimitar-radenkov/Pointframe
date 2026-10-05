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
        if (result.IsError != true)
        {
            return TelemetryOutcome.Success;
        }

        return IsDenied(result.StructuredContent) ? TelemetryOutcome.Denied : TelemetryOutcome.Error;
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

    private static bool IsDenied(JsonElement? structuredContent)
    {
        if (structuredContent is not { ValueKind: JsonValueKind.Object } content)
        {
            return false;
        }

        foreach (var property in content.EnumerateObject())
        {
            if (property.Name.Equals("error", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var errorProperty in property.Value.EnumerateObject())
                {
                    if (errorProperty.Name.Equals("code", StringComparison.OrdinalIgnoreCase)
                        && errorProperty.Value.ValueKind == JsonValueKind.String
                        && _deniedErrorCodes.Contains(errorProperty.Value.GetString()!))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }
}
