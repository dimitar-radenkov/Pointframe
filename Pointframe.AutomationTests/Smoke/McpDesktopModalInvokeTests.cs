using System.Diagnostics;
using System.Text.Json;
using Pointframe.AutomationTests.Support;
using Xunit;

namespace Pointframe.AutomationTests.Smoke;

[Trait("Category", "DesktopAutomation")]
public sealed class McpDesktopModalInvokeTests
{
    [SkippableFact]
    public async Task InvokeThatOpensModalDialogUsesClickAndWorkerRemainsUsable()
    {
        DesktopGateTestSupport.RequireInteractiveGate();
        var fixtureExecutable = RequireFixtureExecutable();
        var mcpExecutable = Environment.GetEnvironmentVariable("POINTFRAME_MCP_EXECUTABLE")!;
        var artifacts = Path.Combine(Path.GetTempPath(), "pointframe-desktop-verify", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifacts);

        await using var harness = await DesktopFixtureHarness.StartAsync(
            fixtureExecutable,
            mcpExecutable,
            artifacts,
            0);

        var display = (await harness.ListDisplaysAsync())[0];
        var (_, openDialog) = await WaitForElementAsync(harness, display, "automationId", "openModalButton");
        var timer = Stopwatch.StartNew();
        var invoke = await harness.InvokeAsync(openDialog.GetProperty("elementRef").GetString()!, TimeSpan.FromSeconds(5));
        timer.Stop();
        var invokeResult = invoke.GetProperty("structuredContent");

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), $"Invoke took {timer.Elapsed}.");
        Assert.Equal("click", invokeResult.GetProperty("method").GetString());
        Assert.Equal("Completed", invokeResult.GetProperty("operationStatus").GetString());
        Assert.Equal("Complete", invokeResult.GetProperty("dispatch").GetString());
        Assert.False(invokeResult.TryGetProperty("error", out var invokeError) && invokeError.ValueKind != JsonValueKind.Null);

        var (dialogObservation, modalText) = await WaitForElementAsync(harness, display, "name", "Modal OK");
        Assert.Equal("Text", modalText.GetProperty("role").GetString());
        // An agent dismisses the dialog the way a user would: through its OK button.
        var okButton = dialogObservation.GetProperty("elements").EnumerateArray().Single(element =>
            element.TryGetProperty("name", out var name) && name.GetString() == "OK"
            && element.GetProperty("windowRef").GetString() == modalText.GetProperty("windowRef").GetString());
        var dismiss = await harness.InvokeAsync(okButton.GetProperty("elementRef").GetString()!);
        Assert.Equal("Complete", dismiss.GetProperty("structuredContent").GetProperty("dispatch").GetString());
        await WaitForElementAbsentAsync(harness, display, "name", "Modal OK");

        var (_, saveButton) = await WaitForElementAsync(harness, display, "automationId", "saveButton");
        var saveInvoke = await harness.InvokeAsync(saveButton.GetProperty("elementRef").GetString()!);
        var saveResult = saveInvoke.GetProperty("structuredContent");
        Assert.Equal("click", saveResult.GetProperty("method").GetString());
        Assert.Equal("Complete", saveResult.GetProperty("dispatch").GetString());
    }

    private static async Task WaitForElementAbsentAsync(
        DesktopFixtureHarness harness,
        FixtureDisplay display,
        string property,
        string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var observation = await harness.ObserveUiAsync(display);
            var present = observation.TryGetProperty("elements", out var elements)
                && elements.EnumerateArray().Any(element => element.TryGetProperty(property, out var value)
                    && value.ValueKind == JsonValueKind.String
                    && string.Equals(value.GetString(), expected, StringComparison.Ordinal));
            if (!present && observation.GetProperty("uiaStatus").GetString() == "Available")
            {
                return;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new Xunit.Sdk.XunitException($"The element with {property} '{expected}' was still present after 15 seconds.");
            }

            await Task.Delay(500);
        }
    }

    // A just-launched fixture or a just-opened dialog can take a moment to appear in UI Automation.
    private static async Task<(JsonElement Observation, JsonElement Element)> WaitForElementAsync(
        DesktopFixtureHarness harness,
        FixtureDisplay display,
        string property,
        string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var observation = await harness.ObserveUiAsync(display);
            var matches = observation.TryGetProperty("elements", out var elements)
                ? elements.EnumerateArray().Where(element => element.TryGetProperty(property, out var value)
                    && value.ValueKind == JsonValueKind.String
                    && string.Equals(value.GetString(), expected, StringComparison.Ordinal)).ToArray()
                : [];
            if (matches.Length == 1)
            {
                return (observation, matches[0]);
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new Xunit.Sdk.XunitException($"No single element with {property} '{expected}' within 15 seconds; matches: {matches.Length}.");
            }

            await Task.Delay(500);
        }
    }

    private static string RequireFixtureExecutable()
    {
        var path = Environment.GetEnvironmentVariable(DesktopFixtureHarness.FixtureExecutableVariable);
        Skip.If(
            string.IsNullOrWhiteSpace(path) || !File.Exists(path),
            $"Set {DesktopFixtureHarness.FixtureExecutableVariable} to the published Pointframe.DesktopTestFixture.exe.");
        return Path.GetFullPath(path!);
    }
}
