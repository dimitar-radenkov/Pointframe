using Pointframe.AutomationTests.Support;
using Xunit;

namespace Pointframe.AutomationTests.Smoke;

public sealed class McpOrdinaryStartupObservationTests
{
    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "DesktopAutomation")]
    public async Task NormalPointframeLaunchSupportsTrayObservationWithoutIdAssumptions(
        bool useAutomationIds)
    {
        var mcpPath = Environment.GetEnvironmentVariable("POINTFRAME_MCP_EXECUTABLE");
        var pointframePath = Environment.GetEnvironmentVariable("POINTFRAME_EXECUTABLE");
        var outputDirectory = Environment.GetEnvironmentVariable("POINTFRAME_MCP_TEST_OUTPUT_DIRECTORY");
        var dedicatedEnvironmentAcknowledged = Environment.GetEnvironmentVariable("POINTFRAME_DESKTOP_TEST_ACK");
        Skip.If(string.IsNullOrWhiteSpace(mcpPath), "Set POINTFRAME_MCP_EXECUTABLE to run the real MCP desktop gate.");
        Skip.If(string.IsNullOrWhiteSpace(pointframePath), "Set POINTFRAME_EXECUTABLE to run the real MCP desktop gate.");
        Skip.If(string.IsNullOrWhiteSpace(outputDirectory), "Set POINTFRAME_MCP_TEST_OUTPUT_DIRECTORY to run the real MCP desktop gate.");
        Skip.If(!string.Equals(dedicatedEnvironmentAcknowledged, "true", StringComparison.OrdinalIgnoreCase),
            "The real desktop gate requires explicit operator acknowledgement that this account or machine is safe for automation.");

        var profile = BlackBoxAppProfileFactory.CreatePointframeProfile(
            pointframePath!,
            ["CTRL", "SHIFT", "P"]);
        var manifest = new McpRunManifest();
        var policyPath = DesktopGatePolicyFactory.Create(pointframePath!, outputDirectory!);
        try
        {
            await using var client = await McpDesktopTestClient.LaunchAsync(
                mcpPath!, ["--desktop-testing", "--desktop-policy", policyPath]).ConfigureAwait(false);
            var navigator = new BlackBoxPointframeNavigator(client, profile, manifest, useAutomationIds);

            await navigator.StartSessionAsync().ConfigureAwait(false);
            var initial = await navigator.ObserveAppAsync().ConfigureAwait(false);
            Assert.False(initial.ValueKind is System.Text.Json.JsonValueKind.Undefined or System.Text.Json.JsonValueKind.Null);

            // This checks ordinary startup and tray observation only. Settings persistence is covered
            // by SettingsWindowSmokeTests and the pointframe verify app specification.
            await navigator.EndSessionAsync().ConfigureAwait(false);
            Assert.True(manifest.OrdinaryStartup);
            Assert.Equal(profile.Arguments, manifest.ActualArguments);
            Assert.Equal(manifest.TargetExecutableSha256Before, manifest.TargetExecutableSha256After);
        }
        finally
        {
            DesktopGatePolicyFactory.Delete(policyPath);
        }
    }
}
