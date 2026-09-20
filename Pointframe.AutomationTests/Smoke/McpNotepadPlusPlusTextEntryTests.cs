using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pointframe.AutomationTests.Support;
using Xunit;
using Xunit.Abstractions;

namespace Pointframe.AutomationTests.Smoke;

/// <summary>
/// Drives a real third-party application — Notepad++ — rather than the purpose-built fixture, to find
/// out what the desktop driver does against a target that was never built to cooperate with it. The
/// fixture asserts its own foreground and reports every event it receives; Notepad++ does neither,
/// so the only evidence available is what the screen shows.
/// </summary>
[Trait("Category", "DesktopAutomation")]
public partial class McpNotepadPlusPlusTextEntryTests(ITestOutputHelper output)
{
    private const string ProfileId = "notepadplusplus";
    private const string TypedText = "hello from agent";
    private const string DefaultInstallPath = @"C:\Program Files\Notepad++\notepad++.exe";

    [SkippableFact]
    public async Task AgentCanOpenNotepadPlusPlusAndTypeALine()
    {
        DesktopGateTestSupport.RequireInteractiveGate();
        var editorPath = Environment.GetEnvironmentVariable("POINTFRAME_NOTEPADPP_EXECUTABLE") ?? DefaultInstallPath;
        Skip.IfNot(File.Exists(editorPath), $"Notepad++ was not found at '{editorPath}'.");
        var mcpPath = Environment.GetEnvironmentVariable("POINTFRAME_MCP_EXECUTABLE")!;

        var artifactDirectory = Path.Combine(Path.GetTempPath(), $"pointframe-npp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(artifactDirectory);

        // A scratch file with numbered lines, so the caret line reported by Notepad++ tells us exactly
        // where a click landed. -nosession and -multiInst keep the user's restored session and any
        // already-running instance completely out of this.
        var scratchPath = Path.Combine(artifactDirectory, "pointframe-scratch.txt");
        var scratch = new StringBuilder();
        for (var line = 1; line <= 40; line++)
        {
            scratch.AppendLine($"line {line:D2} ................................");
        }

        File.WriteAllText(scratchPath, scratch.ToString());
        var scratchBytesBefore = new FileInfo(scratchPath).Length;

        // Notepad++ needs to be told to ignore its restored session and any running instance; plain
        // Notepad takes only the file. The test runs against either, to separate a driver-wide input
        // failure from one specific to Scintilla.
        string[] arguments = Path.GetFileName(editorPath).StartsWith("notepad++", StringComparison.OrdinalIgnoreCase)
            ? ["-nosession", "-multiInst", scratchPath]
            : [scratchPath];
        var policyPath = CreatePolicy(editorPath, artifactDirectory, arguments);

        DesktopFixtureHarness.EnsurePerMonitorDpiAwareness();
        var before = Process.GetProcessesByName(EditorProcessName(editorPath)).Select(process => process.Id).ToHashSet();
        output.WriteLine($"[pre] {EditorProcessName(editorPath)} instances already running: {before.Count}");

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

            startedProcessId = await WaitForEditorAsync(before, EditorProcessName(editorPath));
            var window = await WaitForEditorWindowAsync(client, startedProcessId);
            output.WriteLine($"[window] '{window.Title}' at {window.X},{window.Y} {window.Width}x{window.Height} on {window.Monitor}");
            Assert.Contains("pointframe-scratch", window.Title, StringComparison.OrdinalIgnoreCase);

            var caretBefore = ParseCaret(await ReadWindowTextAsync(client, window));
            output.WriteLine($"[caret before click] {caretBefore}");

            var observation = await ObserveAsync(client, sessionId, window);
            output.WriteLine($"[observe] imageBlocks={observation.ImageBlockCount} preview={observation.Width}x{observation.Height} desktop={window.Width}x{window.Height}");
            Assert.True(observation.ImageBlockCount > 0, "The observation returned no image block.");

            // Halfway down the editing canvas, well inside the left margin.
            var caretX = observation.Width / 3;
            var caretY = observation.Height / 2;
            var click = await client.CallToolAsync(
                "desktop_click",
                new
                {
                    sessionId,
                    actionId = Guid.NewGuid().ToString(),
                    observationRef = observation.ObservationRef,
                    imageRef = observation.ImageRef,
                    x = caretX,
                    y = caretY,
                },
                TimeSpan.FromSeconds(30));
            output.WriteLine($"[click] preview({caretX},{caretY}) -> {click.GetProperty("structuredContent").GetRawText()}");

            // Separate "the pointer went to the wrong place" from "the pointer went to the right place
            // and the app ignored it". Everything downstream hinges on which of the two this is.
            var cursor = DesktopFixtureHarness.GetCursorPosition();
            var expectedX = window.X + (int)Math.Floor(caretX * (double)window.Width / observation.Width);
            var expectedY = window.Y + (int)Math.Floor(caretY * (double)window.Height / observation.Height);
            output.WriteLine($"[cursor] expected desktop ({expectedX},{expectedY}); actual ({cursor.X},{cursor.Y})");
            output.WriteLine($"[verdict] pointer landed where aimed: {Math.Abs(cursor.X - expectedX) <= 2 && Math.Abs(cursor.Y - expectedY) <= 2}");
            output.WriteLine($"[foreground] {DescribeForegroundWindow()}");

            await Task.Delay(500);
            var caretAfterClick = ParseCaret(await ReadWindowTextAsync(client, window));
            output.WriteLine($"[caret after click] {caretAfterClick}");
            var clickMovedCaret = caretAfterClick.Line > 0 && caretAfterClick != caretBefore;
            output.WriteLine($"[verdict] click placed the caret: {clickMovedCaret}");

            // Type without re-clicking: desktop_enter_text clicks its own point first, so give it the
            // same spot and let it do the whole job the way an agent would.
            var freshObservation = await ObserveAsync(client, sessionId, window);
            var typed = await client.CallToolAsync(
                "desktop_enter_text",
                new
                {
                    sessionId,
                    actionId = Guid.NewGuid().ToString(),
                    observationRef = freshObservation.ObservationRef,
                    imageRef = freshObservation.ImageRef,
                    x = caretX,
                    y = caretY,
                    text = TypedText,
                },
                TimeSpan.FromSeconds(60));
            var typedStructured = typed.GetProperty("structuredContent");
            output.WriteLine($"[enter_text] {typedStructured.GetRawText()}");

            await Task.Delay(750);
            var readback = await ReadWindowTextAsync(client, window);
            output.WriteLine($"[readback ocr]\n{readback}");

            var typedLanded = readback is not null
                && readback.Replace(" ", string.Empty).Contains(TypedText, StringComparison.OrdinalIgnoreCase);
            var reportedSuccess = !typedStructured.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.Object;
            output.WriteLine($"[verdict] enter_text reported success: {reportedSuccess}; text actually on screen: {typedLanded}");

            // The scratch file must never be written: nothing here saves, and the process is killed.
            Assert.Equal(scratchBytesBefore, new FileInfo(scratchPath).Length);

            Assert.True(clickMovedCaret, $"The click did not move the caret (before={caretBefore}, after={caretAfterClick}).");
            Assert.True(typedLanded, $"enter_text reported success={reportedSuccess} but the text never appeared. OCR saw:\n{readback}");
        }
        finally
        {
            // Kill rather than close: an unsaved buffer would raise a save prompt.
            KillEditor(startedProcessId, before, EditorProcessName(editorPath));
            TryDeleteDirectory(artifactDirectory);
        }
    }

    private static string DescribeForegroundWindow()
    {
        var handle = NativeMethods.GetForegroundWindow();
        if (handle == nint.Zero)
        {
            return "(no foreground window)";
        }

        var builder = new StringBuilder(512);
        _ = NativeMethods.GetWindowText(handle, builder, builder.Capacity);
        _ = NativeMethods.GetWindowThreadProcessId(handle, out var processId);
        var name = "?";
        try
        {
            using var process = Process.GetProcessById((int)processId);
            name = process.ProcessName;
        }
        catch (ArgumentException)
        {
            // Process already gone.
        }

        return $"pid={processId} ({name}) title='{builder}'";
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern nint GetForegroundWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        internal static extern int GetWindowText(nint hWnd, StringBuilder text, int count);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);
    }

    private static string CreatePolicy(string executablePath, string artifactRoot, string[] arguments)
    {
        var policyPath = Path.Combine(artifactRoot, $"npp-policy-{Guid.NewGuid():N}.json");
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

    private static Caret ParseCaret(string? ocrText)
    {
        if (string.IsNullOrWhiteSpace(ocrText))
        {
            return new Caret(0, 0);
        }

        var line = CaretLineRegex().Match(ocrText);
        var column = CaretColumnRegex().Match(ocrText);
        return new Caret(
            line.Success ? int.Parse(line.Groups[1].Value) : 0,
            column.Success ? int.Parse(column.Groups[1].Value) : 0);
    }

    [GeneratedRegex(@"Ln\s*:?\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex CaretLineRegex();

    [GeneratedRegex(@"Col\s*:?\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex CaretColumnRegex();

    private static async Task<string?> ReadWindowTextAsync(McpDesktopTestClient client, EditorWindow window)
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

    private static async Task<EditorObservation> ObserveAsync(
        McpDesktopTestClient client,
        string sessionId,
        EditorWindow window)
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

        return new EditorObservation(
            structured.GetProperty("observationRef").GetString()!,
            image.GetProperty("imageRef").GetString()!,
            image.GetProperty("width").GetInt32(),
            image.GetProperty("height").GetInt32(),
            blockCount);
    }

    private async Task<EditorWindow> WaitForEditorWindowAsync(McpDesktopTestClient client, int processId)
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
                if (width <= 0 || height <= 0)
                {
                    continue;
                }

                return new EditorWindow(
                    window.GetProperty("title").GetString() ?? string.Empty,
                    bounds.GetProperty("x").GetInt32(),
                    bounds.GetProperty("y").GetInt32(),
                    width,
                    height,
                    window.TryGetProperty("monitorName", out var monitor) ? monitor.GetString() : null);
            }

            await Task.Delay(250);
        }

        throw new InvalidOperationException($"No visible window appeared for notepad++ pid {processId}.");
    }

    private static string EditorProcessName(string editorPath) =>
        Path.GetFileNameWithoutExtension(editorPath);

    private static async Task<int> WaitForEditorAsync(HashSet<int> before, string processName)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var candidate = Process.GetProcessesByName(processName)
                .Select(process => process.Id)
                .FirstOrDefault(id => !before.Contains(id));
            if (candidate != 0)
            {
                return candidate;
            }

            await Task.Delay(200);
        }

        throw new InvalidOperationException($"{processName} did not start.");
    }

    private static void KillEditor(int processId, HashSet<int> before, string processName)
    {
        foreach (var process in Process.GetProcessesByName(processName))
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

    private sealed record Caret(int Line, int Column)
    {
        public override string ToString() => $"Ln {Line}, Col {Column}";
    }

    private sealed record EditorWindow(string Title, int X, int Y, int Width, int Height, string? Monitor);

    private sealed record EditorObservation(
        string ObservationRef,
        string ImageRef,
        int Width,
        int Height,
        int ImageBlockCount);
}
