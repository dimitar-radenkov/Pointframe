using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Pointframe.Mcp;

// Opt-in: replaces the text block that duplicates structuredContent with a short summary, for tools whose
// result is a status rather than data. Tools whose result is the data an agent reads (lists, searches,
// observations, replay differences) keep their full JSON text, because a client that shows only text
// blocks to the model would otherwise lose it. structuredContent is never touched.
internal static class McpCompactTextFilter
{
    private const int MaxValueLength = 300;

    private static readonly HashSet<string> ActionTools = new(StringComparer.Ordinal)
    {
        "desktop_start_test_session",
        "desktop_restart_app",
        "desktop_focus_window",
        "desktop_click",
        "desktop_press_keys",
        "desktop_drag",
        "desktop_enter_text",
        "desktop_invoke",
        "desktop_scroll",
        "desktop_get_action_result",
        "desktop_end_test_session",
    };

    private static readonly HashSet<string> SummarizedTools = new(ActionTools, StringComparer.Ordinal)
    {
        "desktop_check_ui",
        "desktop_get_test_report",
        "start_recording",
        "stop_recording",
        "get_recording_status",
    };

    internal static McpRequestFilter<CallToolRequestParams, CallToolResult> Create()
    {
        return next => async (context, cancellationToken) =>
        {
            var result = await next(context, cancellationToken);
            return Apply(context.Params?.Name, context.Params?.Arguments, result);
        };
    }

    internal static CallToolResult Apply(string? tool, IDictionary<string, JsonElement>? arguments, CallToolResult result)
    {
        if (tool is null || !SummarizedTools.Contains(tool) || result.StructuredContent is not { ValueKind: JsonValueKind.Object } structured)
        {
            return result;
        }

        var index = -1;
        for (var position = 0; position < result.Content.Count; position++)
        {
            if (result.Content[position] is TextContentBlock)
            {
                index = position;
                break;
            }
        }

        if (index < 0)
        {
            return result;
        }

        string? actionId = null;
        if (arguments is not null && arguments.TryGetValue("actionId", out var actionIdValue) && actionIdValue.ValueKind == JsonValueKind.String)
        {
            actionId = actionIdValue.GetString();
        }

        result.Content[index] = new TextContentBlock { Text = Summarize(tool, actionId, structured) };
        return result;
    }

    internal static string Summarize(string tool, string? actionId, JsonElement structured)
    {
        var text = new StringBuilder(tool);
        if (tool == "desktop_get_test_report")
        {
            AppendReport(text, structured);
            return text.ToString();
        }

        // The caller chose the action id and the session, so only the session-creating tools echo them back.
        var echoesIds = tool is "desktop_start_test_session" or "desktop_restart_app" || !ActionTools.Contains(tool);
        AppendProperties(text, structured, depth: 0, echoesIds);
        if (ActionTools.Contains(tool) && structured.TryGetProperty("dispatch", out var dispatch) && dispatch.ValueKind == JsonValueKind.String)
        {
            switch (dispatch.GetString())
            {
                case "Partial":
                case "Unknown":
                    text.Append(". Outcome uncertain: do not resend this action; call desktop_get_action_result").Append(actionId is null ? " with its actionId." : $" with actionId={actionId}.");
                    break;
                case "NotStarted":
                    text.Append(". Not dispatched: no input was sent.");
                    break;
            }
        }

        return text.ToString();
    }

    private static void AppendProperties(StringBuilder text, JsonElement element, int depth, bool echoesIds = true)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (depth == 0 && (property.Name == "schemaVersion" || (!echoesIds && property.Name is "sessionRef" or "targetRef")))
            {
                continue;
            }

            switch (property.Value.ValueKind)
            {
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    break;
                case JsonValueKind.Object when property.Name == "error":
                    text.Append(" error=").Append(Scalar(property.Value, "code")).Append(": ").Append(Scalar(property.Value, "message"));
                    break;
                case JsonValueKind.Object:
                    var inner = new StringBuilder();
                    AppendProperties(inner, property.Value, depth + 1);
                    text.Append(' ').Append(property.Name).Append('{').Append(inner.ToString().TrimStart()).Append('}');
                    break;
                case JsonValueKind.Array:
                    text.Append(' ').Append(property.Name).Append('[').Append(property.Value.GetArrayLength()).Append(']');
                    break;
                default:
                    text.Append(' ').Append(property.Name).Append('=').Append(Format(property.Value));
                    break;
            }
        }
    }

    private static void AppendReport(StringBuilder text, JsonElement report)
    {
        foreach (var name in new[] { "sessionRef", "verdict", "executableSha256", "sessionDirectory" })
        {
            if (report.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                text.Append(' ').Append(name).Append('=').Append(Format(value));
            }
        }

        if (report.TryGetProperty("criteria", out var criteria) && criteria.ValueKind == JsonValueKind.Array)
        {
            text.Append(" criteria[").Append(criteria.GetArrayLength()).Append("]:");
            foreach (var criterion in criteria.EnumerateArray())
            {
                text.Append(' ').Append(Scalar(criterion, "id")).Append('=').Append(Scalar(criterion, "verdict"));
            }
        }

        AppendCountAndFailures(text, report, "actions", "result", "verification");
        AppendCountAndFailures(text, report, "checks", null, "verdict");
        if (report.TryGetProperty("proof", out var proof) && proof.ValueKind == JsonValueKind.Object)
        {
            text.Append(" proof{keyId=").Append(Scalar(proof, "keyId")).Append(" rootHash=").Append(Scalar(proof, "rootHash")).Append('}');
        }

        text.Append(". Full signed report.json and evidence are in sessionDirectory.");
    }

    private static void AppendCountAndFailures(StringBuilder text, JsonElement report, string name, string? nested, string statusProperty)
    {
        if (!report.TryGetProperty(name, out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        text.Append(' ').Append(name).Append('[').Append(items.GetArrayLength()).Append(']');
        var failures = new List<string>();
        foreach (var item in items.EnumerateArray())
        {
            var source = nested is not null && item.TryGetProperty(nested, out var inner) ? inner : item;
            var status = Scalar(source, statusProperty);
            if (status is "Failed" or "Inconclusive")
            {
                failures.Add($"{Scalar(item, "description")} ({status})");
            }
        }

        if (failures.Count > 0)
        {
            text.Append(" not-passed: ").Append(string.Join("; ", failures));
        }
    }

    private static string Scalar(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? Format(value)
            : string.Empty;
    }

    private static string Format(JsonElement value)
    {
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => value.GetRawText(),
        };
        return text.Length > MaxValueLength ? text[..MaxValueLength] + "..." : text;
    }
}
