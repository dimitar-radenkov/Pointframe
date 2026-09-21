using System.Diagnostics;
using System.Text.Json;
using Pointframe.AutomationTests.Support;
using Xunit;
using Xunit.Abstractions;

namespace Pointframe.AutomationTests.Smoke;

/// <summary>
/// The third data point in the input-compatibility comparison. The WinForms fixture accepts driver
/// input but asserts its own foreground and topmost state; Notepad++ (Scintilla) accepts none. This
/// drives Pointframe's own WPF Library window, which does neither, to establish whether the driver
/// works on ordinary applications or only on the fixture built to cooperate with it.
/// </summary>
[Trait("Category", "DesktopAutomation")]
public class McpWpfInputComparisonTests(ITestOutputHelper output)
{
    private const string ProfileId = "pointframe-wpf";
    private const string TypedText = "hello from agent";

    [SkippableFact]
    [Trait("Category", "DesktopAutomation")]
    public async Task UiAutomationWindowReferencesCanFocusTheWpfTarget()
    {
        DesktopGateTestSupport.RequireInteractiveGate();
        var appPath = Environment.GetEnvironmentVariable("POINTFRAME_EXECUTABLE")!;
        var mcpPath = Environment.GetEnvironmentVariable("POINTFRAME_MCP_EXECUTABLE")!;
        Skip.IfNot(File.Exists(appPath), $"Pointframe was not found at '{appPath}'.");

        var artifactDirectory = Path.Combine(Path.GetTempPath(), $"pointframe-wpf-window-ref-{Guid.NewGuid():N}");
        Directory.CreateDirectory(artifactDirectory);
        var policyPath = CreatePolicy(appPath, artifactDirectory, ["--automation-open-library"]);

        DesktopFixtureHarness.EnsurePerMonitorDpiAwareness();
        var before = Process.GetProcessesByName("Pointframe").Select(process => process.Id).ToHashSet();
        await using var client = await McpDesktopTestClient.LaunchAsync(
            mcpPath,
            ["--desktop-testing", "--desktop-policy", policyPath]);

        var startedProcessId = 0;
        try
        {
            var start = await client.CallToolAsync(
                "desktop_start_test_session",
                new { actionId = Guid.NewGuid().ToString(), profileId = ProfileId },
                TimeSpan.FromSeconds(60));
            var sessionId = start.GetProperty("structuredContent").GetProperty("sessionRef").GetString()!;

            startedProcessId = await WaitForProcessAsync(before);
            var window = await WaitForWindowAsync(client, startedProcessId);
            var elements = await ObserveElementsAsync(client, sessionId, window);
            var windowRefs = elements.WindowRefs.Distinct(StringComparer.Ordinal).ToArray();

            output.WriteLine($"[uia] status={elements.Status} count={elements.Count} windowRefs={string.Join(", ", windowRefs)}");
            Assert.Equal("Available", elements.Status);
            Assert.NotEmpty(windowRefs);

            var focus = await client.CallToolAsync(
                "desktop_focus_window",
                new
                {
                    sessionId,
                    actionId = Guid.NewGuid().ToString(),
                    windowRef = windowRefs[0],
                },
                TimeSpan.FromSeconds(30));
            var dispatch = focus.GetProperty("structuredContent").GetProperty("dispatch").GetString();
            output.WriteLine($"[focus] windowRef={windowRefs[0]} dispatch={dispatch}");
            Assert.Equal("Complete", dispatch);
        }
        finally
        {
            KillProcess(startedProcessId, before);
            DesktopGatePolicyFactory.Delete(policyPath);
            TryDeleteDirectory(artifactDirectory);
        }
    }

    [SkippableFact]
    public async Task DriverInputReachesAWpfWindow()
    {
        DesktopGateTestSupport.RequireInteractiveGate();
        var appPath = Environment.GetEnvironmentVariable("POINTFRAME_EXECUTABLE")!;
        var mcpPath = Environment.GetEnvironmentVariable("POINTFRAME_MCP_EXECUTABLE")!;
        Skip.IfNot(File.Exists(appPath), $"Pointframe was not found at '{appPath}'.");

        var artifactDirectory = Path.Combine(Path.GetTempPath(), $"pointframe-wpf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(artifactDirectory);
        var policyPath = CreatePolicy(appPath, artifactDirectory, ["--automation-open-library"]);

        DesktopFixtureHarness.EnsurePerMonitorDpiAwareness();
        var before = Process.GetProcessesByName("Pointframe").Select(process => process.Id).ToHashSet();
        output.WriteLine($"[pre] Pointframe instances already running: {before.Count}");

        await using var client = await McpDesktopTestClient.LaunchAsync(
            mcpPath,
            ["--desktop-testing", "--desktop-policy", policyPath]);

        var startedProcessId = 0;
        try
        {
            var start = await client.CallToolAsync(
                "desktop_start_test_session",
                new { actionId = Guid.NewGuid().ToString(), profileId = ProfileId },
                TimeSpan.FromSeconds(60));
            var sessionId = start.GetProperty("structuredContent").GetProperty("sessionRef").GetString()!;

            startedProcessId = await WaitForProcessAsync(before);
            var window = await WaitForWindowAsync(client, startedProcessId);
            output.WriteLine($"[window] '{window.Title}' at {window.X},{window.Y} {window.Width}x{window.Height} on {window.Monitor}");

            var observation = await ObserveAsync(client, sessionId, window);
            output.WriteLine($"[observe] imageBlocks={observation.ImageBlockCount} preview={observation.Width}x{observation.Height} desktop={window.Width}x{window.Height}");
            Assert.True(observation.ImageBlockCount > 0, "The observation returned no image block.");

            // Defect 1 check: elements must actually come back now that the UIA backend is registered.
            var elements = await ObserveElementsAsync(client, sessionId, window);
            output.WriteLine($"[uia] status={elements.Status} count={elements.Count} error={elements.ErrorCode}");
            foreach (var described in elements.Described.Take(8))
            {
                output.WriteLine($"[uia element] {described}");
            }

            Assert.Equal("Available", elements.Status);
            Assert.True(elements.Count > 0, $"No UI Automation elements were returned (error={elements.ErrorCode}).");
            Assert.All(elements.Refs, reference => Assert.StartsWith("el-", reference));

            // The search field sits in the upper portion of the Library window, spanning most of its
            // width. A point a third across and a fifth down lands inside it at the default size.
            var targetX = observation.Width / 3;
            var targetY = observation.Height / 5;

            var typed = await client.CallToolAsync(
                "desktop_enter_text",
                new
                {
                    sessionId,
                    actionId = Guid.NewGuid().ToString(),
                    observationRef = observation.ObservationRef,
                    imageRef = observation.ImageRef,
                    x = targetX,
                    y = targetY,
                    text = TypedText,
                },
                TimeSpan.FromSeconds(60));
            output.WriteLine($"[enter_text] {typed.GetProperty("structuredContent").GetRawText()}");

            var cursor = DesktopFixtureHarness.GetCursorPosition();
            var expectedX = window.X + (int)Math.Floor(targetX * (double)window.Width / observation.Width);
            var expectedY = window.Y + (int)Math.Floor(targetY * (double)window.Height / observation.Height);
            output.WriteLine($"[cursor] expected ({expectedX},{expectedY}); actual ({cursor.X},{cursor.Y})");

            await Task.Delay(1000);
            var afterText = await ReadWindowTextAsync(client, window);
            output.WriteLine($"[ocr after]\n{afterText}");

            var landed = afterText is not null
                && afterText.Replace(" ", string.Empty).Contains(
                    TypedText.Replace(" ", string.Empty),
                    StringComparison.OrdinalIgnoreCase);
            output.WriteLine($"[verdict] text reached the WPF window: {landed}");

            Assert.True(landed, $"The typed text never reached the WPF window. OCR saw:\n{afterText}");
        }
        finally
        {
            KillProcess(startedProcessId, before);
            DesktopGatePolicyFactory.Delete(policyPath);
            TryDeleteDirectory(artifactDirectory);
        }
    }

    private static string CreatePolicy(string executablePath, string artifactRoot, string[] arguments)
    {
        var policyPath = Path.Combine(artifactRoot, $"wpf-policy-{Guid.NewGuid():N}.json");
        var policy = new
        {
            schemaVersion = 1,
            artifactRoot,
            evidencePolicy = "All",
            profiles = new[]
            {
                new
                {
                    id = ProfileId,
                    executablePath = Path.GetFullPath(executablePath),
                    arguments,
                    workingDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath))!,
                    allowAttach = false,
                    allowedActions = new[]
                    {
                        "ListApps", "StartTestSession", "RestartApp", "ObserveApp", "FocusWindow",
                        "Click", "PressKeys", "Drag", "EnterText", "Invoke", "CheckUi", "Scroll",
                        "GetActionResult", "GetTestReport", "EndTestSession",
                    },
                    allowedGlobalHotkeys = new { },
                    allowedShellSurfaces = Array.Empty<string>(),
                    allowMonitorObservation = true,
                },
            },
        };
        File.WriteAllText(policyPath, JsonSerializer.Serialize(policy, new JsonSerializerOptions { WriteIndented = true }));
        return policyPath;
    }

    private static async Task<string?> ReadWindowTextAsync(McpDesktopTestClient client, TargetWindow window)
    {
        var displays = await client.CallToolAsync("list_displays", new { }, TimeSpan.FromSeconds(30));
        var monitor = displays.GetProperty("structuredContent").GetProperty("displays").EnumerateArray()
            .FirstOrDefault(display => display.GetProperty("monitorName").GetString() == window.Monitor);
        if (monitor.ValueKind != JsonValueKind.Object)
        {
            return "(monitor not resolved)";
        }

        var bounds = monitor.GetProperty("boundsPixels");
        var monitorX = bounds.GetProperty("x").GetInt32();
        var monitorY = bounds.GetProperty("y").GetInt32();
        var monitorWidth = bounds.GetProperty("width").GetInt32();
        var monitorHeight = bounds.GetProperty("height").GetInt32();

        var regionX = Math.Clamp(window.X - monitorX, 0, Math.Max(0, monitorWidth - 1));
        var regionY = Math.Clamp(window.Y - monitorY, 0, Math.Max(0, monitorHeight - 1));
        var regionWidth = Math.Clamp(window.Width, 1, monitorWidth - regionX);
        var regionHeight = Math.Clamp(window.Height, 1, monitorHeight - regionY);

        var result = await client.CallToolAsync(
            "read_text_from_monitor",
            new
            {
                monitorName = window.Monitor,
                region = new { x = regionX, y = regionY, width = regionWidth, height = regionHeight },
                includeImage = false,
            },
            TimeSpan.FromSeconds(60));
        var structured = result.GetProperty("structuredContent");
        return structured.TryGetProperty("recognizedText", out var text) ? text.GetString() : structured.GetRawText();
    }

    private static async Task<ElementProbe> ObserveElementsAsync(
        McpDesktopTestClient client,
        string sessionId,
        TargetWindow window)
    {
        var result = await client.CallToolAsync(
            "desktop_observe_app",
            new
            {
                sessionId,
                captureBoundsPixels = new[]
                {
                    new { x = window.X, y = window.Y, width = window.Width, height = window.Height },
                },
                includeUiAutomation = true,
                includeImages = false,
            },
            TimeSpan.FromSeconds(60));

        var structured = result.GetProperty("structuredContent");
        var elements = structured.TryGetProperty("elements", out var elementArray)
            && elementArray.ValueKind == JsonValueKind.Array
                ? elementArray.EnumerateArray().ToArray()
                : [];

        static string Field(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;

        return new ElementProbe(
            structured.TryGetProperty("uiaStatus", out var status) ? status.GetString() ?? "?" : "?",
            elements.Length,
            elements.Select(element => Field(element, "elementRef")).ToArray(),
            elements.Select(element => Field(element, "windowRef"))
                .Where(reference => !string.IsNullOrWhiteSpace(reference))
                .ToArray(),
            elements.Select(element =>
                $"{Field(element, "elementRef")} {Field(element, "role")} " +
                $"name='{Field(element, "name")}' id='{Field(element, "automationId")}' " +
                $"text='{Field(element, "text")}' toggle='{Field(element, "toggleState")}'").ToArray(),
            structured.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("code", out var code)
                    ? code.GetString()
                    : null);
    }

    private sealed record ElementProbe(
        string Status,
        int Count,
        IReadOnlyList<string> Refs,
        IReadOnlyList<string> WindowRefs,
        IReadOnlyList<string> Described,
        string? ErrorCode);

    private static async Task<TargetObservation> ObserveAsync(
        McpDesktopTestClient client,
        string sessionId,
        TargetWindow window)
    {
        var result = await client.CallToolAsync(
            "desktop_observe_app",
            new
            {
                sessionId,
                captureBoundsPixels = new[]
                {
                    new { x = window.X, y = window.Y, width = window.Width, height = window.Height },
                },
                includeUiAutomation = false,
            },
            TimeSpan.FromSeconds(60));

        var structured = result.GetProperty("structuredContent");
        var image = structured.GetProperty("images").EnumerateArray().First();
        var blockCount = result.GetProperty("content").EnumerateArray()
            .Count(block => block.TryGetProperty("type", out var type) && type.GetString() == "image");

        return new TargetObservation(
            structured.GetProperty("observationRef").GetString()!,
            image.GetProperty("imageRef").GetString()!,
            image.GetProperty("width").GetInt32(),
            image.GetProperty("height").GetInt32(),
            blockCount);
    }

    private async Task<TargetWindow> WaitForWindowAsync(McpDesktopTestClient client, int processId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var result = await client.CallToolAsync("list_windows", new { }, TimeSpan.FromSeconds(30));
            foreach (var window in result.GetProperty("structuredContent").GetProperty("windows").EnumerateArray())
            {
                if (window.GetProperty("processId").GetInt32() != processId
                    || window.GetProperty("isMinimized").GetBoolean())
                {
                    continue;
                }

                var bounds = window.GetProperty("boundsPixels");
                var width = bounds.GetProperty("width").GetInt32();
                var height = bounds.GetProperty("height").GetInt32();

                // Skip the tray app's hidden zero-size helper windows.
                if (width < 200 || height < 200)
                {
                    continue;
                }

                return new TargetWindow(
                    window.GetProperty("title").GetString() ?? string.Empty,
                    bounds.GetProperty("x").GetInt32(),
                    bounds.GetProperty("y").GetInt32(),
                    width,
                    height,
                    window.TryGetProperty("monitorName", out var monitor) ? monitor.GetString() : null);
            }

            await Task.Delay(250);
        }

        throw new InvalidOperationException($"No visible window appeared for Pointframe pid {processId}.");
    }

    private static async Task<int> WaitForProcessAsync(HashSet<int> before)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var candidate = Process.GetProcessesByName("Pointframe")
                .Select(process => process.Id)
                .FirstOrDefault(id => !before.Contains(id));
            if (candidate != 0)
            {
                return candidate;
            }

            await Task.Delay(200);
        }

        throw new InvalidOperationException("Pointframe did not start.");
    }

    private static void KillProcess(int processId, HashSet<int> before)
    {
        foreach (var process in Process.GetProcessesByName("Pointframe"))
        {
            using (process)
            {
                if (before.Contains(process.Id) || (processId != 0 && process.Id != processId))
                {
                    continue;
                }

                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
                catch (InvalidOperationException)
                {
                    // Already gone.
                }
            }
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Evidence files may still be held; a temp directory is harmless.
        }
    }

    private sealed record TargetWindow(string Title, int X, int Y, int Width, int Height, string? Monitor);

    private sealed record TargetObservation(
        string ObservationRef,
        string ImageRef,
        int Width,
        int Height,
        int ImageBlockCount);
}
