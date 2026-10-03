using System.Diagnostics;
using System.Text.Json;

namespace Pointframe.Cli;

internal sealed class McpStdioHealthChecker : IMcpHealthChecker
{
    internal static readonly IReadOnlyList<string> ExpectedTools =
    [
        "search_captures", "get_capture", "list_displays", "list_windows", "capture_window",
        "read_text_from_window", "capture_monitor", "read_text_from_monitor", "start_recording",
        "stop_recording", "get_recording_status",
    ];

    public async Task<McpHealthResult> CheckAsync(string executablePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(executablePath))
        {
            return new McpHealthResult(false, "package_not_installed", "The configured MCP executable does not exist.");
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(15));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        var started = false;

        try
        {
            if (!process.Start())
            {
                return new McpHealthResult(false, "handshake_failed", "The MCP process could not be started.");
            }

            started = true;

            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"Pointframe CLI doctor\",\"version\":\"1.0.0\"}}}");
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\",\"params\":{}}");
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{}}");
            await process.StandardInput.FlushAsync(timeoutSource.Token);

            JsonDocument? toolsResponse = null;
            for (var responseCount = 0; responseCount < 4 && toolsResponse is null; responseCount++)
            {
                var line = await process.StandardOutput.ReadLineAsync(timeoutSource.Token);
                if (line is null)
                {
                    break;
                }

                var response = JsonDocument.Parse(line);
                if (response.RootElement.TryGetProperty("id", out var id) && id.TryGetInt32(out var idValue) && idValue == 2)
                {
                    toolsResponse = response;
                }
                else
                {
                    response.Dispose();
                }
            }

            using (toolsResponse)
            {
                if (toolsResponse is null)
                {
                    return new McpHealthResult(false, "handshake_failed", "The MCP server did not return tools/list.");
                }

                var tools = toolsResponse.RootElement.GetProperty("result").GetProperty("tools")
                    .EnumerateArray()
                    .Select(tool => tool.GetProperty("name").GetString())
                    .Where(name => name is not null)
                    .Cast<string>()
                    .ToArray();
                if (!tools.Order(StringComparer.Ordinal).SequenceEqual(ExpectedTools.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                {
                    return new McpHealthResult(false, "unexpected_tool_surface", "The MCP server returned an unexpected direct tool set.", tools);
                }

                return new McpHealthResult(true, "healthy", "The MCP server initialized and advertised the expected tools.", tools);
            }
        }
        catch (OperationCanceledException)
        {
            return new McpHealthResult(false, "handshake_failed", "The MCP handshake timed out.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException or IOException)
        {
            return new McpHealthResult(false, "handshake_failed", exception.Message);
        }
        finally
        {
            if (started && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }
}
