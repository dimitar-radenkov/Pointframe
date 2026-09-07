using System.Text.Json;
using Xunit;

namespace Pointframe.AutomationTests.Support;

public sealed class DesktopGatePolicyFactoryTests
{
    [Fact]
    public void Create_WritesAbsoluteOrdinaryPointframeProfile()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"pointframe-policy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);
        var executablePath = Path.Combine(outputDirectory, "Pointframe.exe");
        File.WriteAllBytes(executablePath, [0]);

        try
        {
            var policyPath = DesktopGatePolicyFactory.Create(executablePath, outputDirectory);
            using var document = JsonDocument.Parse(File.ReadAllText(policyPath));
            var profile = document.RootElement.GetProperty("profiles")[0];

            Assert.Equal(executablePath, profile.GetProperty("executablePath").GetString());
            Assert.Empty(profile.GetProperty("arguments").EnumerateArray());
            Assert.Contains(
                "StartTestSession",
                profile.GetProperty("allowedActions").EnumerateArray().Select(item => item.GetString()));
            Assert.Contains(
                "EndTestSession",
                profile.GetProperty("allowedActions").EnumerateArray().Select(item => item.GetString()));
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [SkippableFact]
    [Trait("Category", "DesktopAutomation")]
    public async Task EnabledMcpLaunch_RegistersDesktopTools()
    {
        var mcpPath = Environment.GetEnvironmentVariable("POINTFRAME_MCP_EXECUTABLE");
        var pointframePath = Environment.GetEnvironmentVariable("POINTFRAME_EXECUTABLE");
        var outputDirectory = Environment.GetEnvironmentVariable("POINTFRAME_MCP_TEST_OUTPUT_DIRECTORY");
        Skip.If(string.IsNullOrWhiteSpace(mcpPath), "Set POINTFRAME_MCP_EXECUTABLE to run the enabled MCP check.");
        Skip.If(string.IsNullOrWhiteSpace(pointframePath), "Set POINTFRAME_EXECUTABLE to run the enabled MCP check.");
        Skip.If(string.IsNullOrWhiteSpace(outputDirectory), "Set POINTFRAME_MCP_TEST_OUTPUT_DIRECTORY to run the enabled MCP check.");

        var policyPath = DesktopGatePolicyFactory.Create(pointframePath!, outputDirectory!);
        try
        {
            await using var client = await McpDesktopTestClient.LaunchAsync(
                mcpPath!,
                ["--desktop-testing", "--desktop-policy", policyPath]).ConfigureAwait(false);
            var tools = await client.ListToolsAsync().ConfigureAwait(false);
            var names = tools
                .GetProperty("tools")
                .EnumerateArray()
                .Select(tool => tool.GetProperty("name").GetString())
                .ToArray();

            Assert.Contains("start_test_session", names);
            Assert.Contains("observe_app", names);
            Assert.Contains("end_test_session", names);
        }
        finally
        {
            DesktopGatePolicyFactory.Delete(policyPath);
        }
    }
}
