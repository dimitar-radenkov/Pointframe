using Pointframe.AutomationTests.Support;
using Xunit;

namespace Pointframe.AutomationTests.Smoke;

public sealed class McpSettingsPersistenceTests
{
    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "DesktopAutomation")]
    public async Task NormalPointframeLaunchSupportsTraySettingsPersistenceWithoutIdAssumptions(
        bool useAutomationIds)
    {
        var mcpPath = Environment.GetEnvironmentVariable("POINTFRAME_MCP_EXECUTABLE");
        var pointframePath = Environment.GetEnvironmentVariable("POINTFRAME_EXECUTABLE");
        var dedicatedEnvironmentAcknowledged = Environment.GetEnvironmentVariable("POINTFRAME_DESKTOP_TEST_ACK");
        Skip.If(string.IsNullOrWhiteSpace(mcpPath), "Set POINTFRAME_MCP_EXECUTABLE to run the real MCP desktop gate.");
        Skip.If(string.IsNullOrWhiteSpace(pointframePath), "Set POINTFRAME_EXECUTABLE to run the real MCP desktop gate.");
        Skip.If(!string.Equals(dedicatedEnvironmentAcknowledged, "true", StringComparison.OrdinalIgnoreCase),
            "The real desktop gate requires explicit operator acknowledgement that this account or machine is safe for automation.");

        var profile = BlackBoxAppProfileFactory.CreatePointframeProfile(
            pointframePath!,
            ["CTRL", "SHIFT", "P"]);
        var manifest = new McpRunManifest();
        await using var client = await McpDesktopTestClient.LaunchAsync(mcpPath!).ConfigureAwait(false);
        var navigator = new BlackBoxPointframeNavigator(client, profile, manifest, useAutomationIds);

        await navigator.StartSessionAsync().ConfigureAwait(false);
        var initial = await navigator.ObserveAppAsync().ConfigureAwait(false);
        Assert.False(initial.ValueKind is System.Text.Json.JsonValueKind.Undefined or System.Text.Json.JsonValueKind.Null);

        // Settings interaction remains intentionally external: this test never seeds private settings
        // or treats Save as process exit. The enabled driver supplies the tray/surface actions.
        await navigator.EndSessionAsync().ConfigureAwait(false);
        Assert.True(manifest.OrdinaryStartup);
        Assert.Equal(profile.Arguments, manifest.ActualArguments);
        Assert.Equal(manifest.TargetExecutableSha256Before, manifest.TargetExecutableSha256After);
    }
}
