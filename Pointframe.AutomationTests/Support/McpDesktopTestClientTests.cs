using System.Text.Json;
using Xunit;

namespace Pointframe.AutomationTests.Support;

public sealed class McpDesktopTestClientTests
{
    [Fact]
    public async Task LaunchesNormalStdioServerAndListsDirectToolsWithoutDesktopAcquisition()
    {
        var executablePath = FindMcpExecutable();
        await using var client = await McpDesktopTestClient.LaunchAsync(executablePath);

        var result = await client.ListToolsAsync();
        Assert.Equal(JsonValueKind.Array, result.GetProperty("tools").ValueKind);
        var names = result.GetProperty("tools")
            .EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString())
            .Where(name => name is not null)
            .ToArray();

        Assert.Contains("list_displays", names);
        Assert.Contains("capture_monitor", names);
        Assert.DoesNotContain(names, name => name!.StartsWith("desktop_", StringComparison.Ordinal));
    }

    private static string FindMcpExecutable()
    {
        var explicitPath = Environment.GetEnvironmentVariable("POINTFRAME_MCP_EXECUTABLE");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            return explicitPath;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "Pointframe.Mcp",
                "bin",
                "Debug",
                "net10.0-windows10.0.18362.0",
                "Pointframe.Mcp.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("The built Pointframe.Mcp executable was not found.");
    }
}
