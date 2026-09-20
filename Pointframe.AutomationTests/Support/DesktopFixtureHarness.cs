using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Pointframe.AutomationTests.Support;

public sealed record FixtureObservation(
    string ObservationRef,
    IReadOnlyList<FixtureObservationImage> Images,
    int ImageBlockCount,
    IReadOnlyList<byte[]> ImageBlockBytes,
    string RawStructuredJson);

public sealed record FixtureObservationImage(
    string ImageRef,
    int Width,
    int Height,
    int DesktopX,
    int DesktopY,
    int DesktopWidth,
    int DesktopHeight);

public sealed record FixtureDisplay(string MonitorName, int X, int Y, int Width, int Height);

// Drives the desktop-testing MCP surface against Pointframe.DesktopTestFixture only. Every target is
// located by scanning the preview image the observation returns, so nothing here depends on knowing
// where the fixture window landed, and nothing here can reach an application the operator owns.
public sealed class DesktopFixtureHarness : IAsyncDisposable
{
    public const string FixtureExecutableVariable = "POINTFRAME_FIXTURE_EXECUTABLE";
    public const string FixtureProfileId = "fixture";

    public static readonly Color ClickTargetColor = Color.FromArgb(255, 0, 255);
    public static readonly Color DragSurfaceColor = Color.FromArgb(0, 255, 0);
    public static readonly Color ScrollSurfaceColor = Color.FromArgb(0, 255, 255);

    private readonly McpDesktopTestClient _client;
    private readonly string _policyPath;
    private readonly string _statePath;
    private readonly int _fixtureProcessId;
    private string? _sessionId;

    private static int _dpiAwarenessSet;

    private DesktopFixtureHarness(
        McpDesktopTestClient client,
        string policyPath,
        string statePath,
        string sessionId,
        int fixtureProcessId)
    {
        _client = client;
        _policyPath = policyPath;
        _statePath = statePath;
        _sessionId = sessionId;
        _fixtureProcessId = fixtureProcessId;
    }

    public string SessionId => _sessionId ?? throw new InvalidOperationException("The session has ended.");

    public string StatePath => _statePath;

    public int FixtureProcessId => _fixtureProcessId;

    public static async Task<DesktopFixtureHarness> StartAsync(
        string fixtureExecutablePath,
        string mcpExecutablePath,
        string artifactDirectory,
        int monitorIndex,
        CancellationToken cancellationToken = default)
    {
        EnsurePerMonitorDpiAwareness();
        Directory.CreateDirectory(artifactDirectory);
        var statePath = Path.Combine(artifactDirectory, $"fixture-state-{Guid.NewGuid():N}.json");

        // The MCP server inherits this environment and passes it on to the fixture it launches, which
        // is how the fixture learns where to report and which monitor to sit on.
        Environment.SetEnvironmentVariable("POINTFRAME_FIXTURE_STATE_PATH", statePath);
        Environment.SetEnvironmentVariable("POINTFRAME_FIXTURE_MONITOR", monitorIndex.ToString());

        var policyPath = DesktopGatePolicyFactory.Create(fixtureExecutablePath, artifactDirectory, FixtureProfileId);
        var client = await McpDesktopTestClient.LaunchAsync(
            mcpExecutablePath,
            ["--desktop-testing", "--desktop-policy", policyPath],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var before = Process.GetProcessesByName("Pointframe.DesktopTestFixture").Select(item => item.Id).ToHashSet();
        var start = await client.CallToolAsync(
            "desktop_start_test_session",
            new { actionId = Guid.NewGuid().ToString(), profileId = FixtureProfileId },
            TimeSpan.FromSeconds(60),
            cancellationToken).ConfigureAwait(false);
        var structured = start.GetProperty("structuredContent");
        var sessionRef = structured.TryGetProperty("sessionRef", out var element) ? element.GetString() : null;
        if (string.IsNullOrWhiteSpace(sessionRef))
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"The fixture session did not start: {structured}");
        }

        var fixtureProcessId = await WaitForFixtureAsync(before, cancellationToken).ConfigureAwait(false);
        await WaitForStateAsync(statePath, cancellationToken).ConfigureAwait(false);
        return new DesktopFixtureHarness(client, policyPath, statePath, sessionRef, fixtureProcessId);
    }

    public async Task<IReadOnlyList<FixtureDisplay>> ListDisplaysAsync(CancellationToken cancellationToken = default)
    {
        var result = await _client.CallToolAsync("list_displays", new { }, TimeSpan.FromSeconds(30), cancellationToken)
            .ConfigureAwait(false);
        var displays = result.GetProperty("structuredContent").GetProperty("displays");
        return displays.EnumerateArray()
            .Select(display =>
            {
                var bounds = display.GetProperty("boundsPixels");
                return new FixtureDisplay(
                    display.GetProperty("monitorName").GetString()!,
                    bounds.GetProperty("x").GetInt32(),
                    bounds.GetProperty("y").GetInt32(),
                    bounds.GetProperty("width").GetInt32(),
                    bounds.GetProperty("height").GetInt32());
            })
            .ToArray();
    }

    public async Task<FixtureObservation> ObserveAsync(
        FixtureDisplay display,
        bool includeImages = true,
        CancellationToken cancellationToken = default)
    {
        var result = await _client.CallToolAsync(
            "desktop_observe_app",
            new
            {
                sessionId = SessionId,
                captureBoundsPixels = new[]
                {
                    new { x = display.X, y = display.Y, width = display.Width, height = display.Height },
                },
                includeUiAutomation = false,
                includeImages,
            },
            TimeSpan.FromSeconds(60),
            cancellationToken).ConfigureAwait(false);

        var structured = result.GetProperty("structuredContent");
        var images = structured.GetProperty("images").EnumerateArray()
            .Select(image =>
            {
                var bounds = image.GetProperty("desktopBoundsPixels");
                return new FixtureObservationImage(
                    image.GetProperty("imageRef").GetString()!,
                    image.GetProperty("width").GetInt32(),
                    image.GetProperty("height").GetInt32(),
                    bounds.GetProperty("x").GetInt32(),
                    bounds.GetProperty("y").GetInt32(),
                    bounds.GetProperty("width").GetInt32(),
                    bounds.GetProperty("height").GetInt32());
            })
            .ToArray();

        var blocks = result.GetProperty("content").EnumerateArray()
            .Where(block => block.TryGetProperty("type", out var type) && type.GetString() == "image")
            .Select(block => Convert.FromBase64String(block.GetProperty("data").GetString()!))
            .ToArray();

        return new FixtureObservation(
            structured.GetProperty("observationRef").GetString()!,
            images,
            blocks.Length,
            blocks,
            structured.GetRawText());
    }

    public Task<JsonElement> ClickAsync(
        FixtureObservation observation,
        int imageX,
        int imageY,
        CancellationToken cancellationToken = default) =>
        _client.CallToolAsync(
            "desktop_click",
            new
            {
                sessionId = SessionId,
                actionId = Guid.NewGuid().ToString(),
                observationRef = observation.ObservationRef,
                imageRef = observation.Images[0].ImageRef,
                x = imageX,
                y = imageY,
            },
            TimeSpan.FromSeconds(30),
            cancellationToken);

    public Task<JsonElement> DragAsync(
        FixtureObservation observation,
        IReadOnlyList<(int X, int Y)> points,
        int durationMilliseconds,
        CancellationToken cancellationToken = default) =>
        _client.CallToolAsync(
            "desktop_drag",
            new
            {
                sessionId = SessionId,
                actionId = Guid.NewGuid().ToString(),
                observationRef = observation.ObservationRef,
                imageRef = observation.Images[0].ImageRef,
                points = points.Select(point => new { x = point.X, y = point.Y, width = 1, height = 1 }).ToArray(),
                durationMilliseconds,
            },
            TimeSpan.FromSeconds(60),
            cancellationToken);

    public Task<JsonElement> EnterTextAsync(
        FixtureObservation observation,
        int imageX,
        int imageY,
        string text,
        CancellationToken cancellationToken = default) =>
        _client.CallToolAsync(
            "desktop_enter_text",
            new
            {
                sessionId = SessionId,
                actionId = Guid.NewGuid().ToString(),
                observationRef = observation.ObservationRef,
                imageRef = observation.Images[0].ImageRef,
                x = imageX,
                y = imageY,
                text,
            },
            TimeSpan.FromSeconds(60),
            cancellationToken);

    public Task<JsonElement> CheckUiAsync(
        string kind,
        string? automationId = null,
        string? expected = null,
        int timeoutSeconds = 5,
        CancellationToken cancellationToken = default) =>
        _client.CallToolAsync(
            "desktop_check_ui",
            new
            {
                sessionId = SessionId,
                kind,
                automationId,
                expected,
                timeoutSeconds,
            },
            TimeSpan.FromSeconds(timeoutSeconds + 20),
            cancellationToken);

    public Task<JsonElement> ScrollAsync(
        FixtureObservation observation,
        int imageX,
        int imageY,
        int detents,
        CancellationToken cancellationToken = default) =>
        _client.CallToolAsync(
            "desktop_scroll",
            new
            {
                sessionId = SessionId,
                actionId = Guid.NewGuid().ToString(),
                observationRef = observation.ObservationRef,
                imageRef = observation.Images[0].ImageRef,
                x = imageX,
                y = imageY,
                detents,
            },
            TimeSpan.FromSeconds(30),
            cancellationToken);

    public FixtureState ReadState()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                return FixtureState.Parse(File.ReadAllText(_statePath));
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                Thread.Sleep(50);
            }
        }

        throw new InvalidOperationException($"The fixture state file could not be read: {_statePath}");
    }

    public FixtureState WaitForState(Func<FixtureState, bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var state = ReadState();
        while (DateTime.UtcNow < deadline)
        {
            state = ReadState();
            if (predicate(state))
            {
                return state;
            }

            Thread.Sleep(100);
        }

        return state;
    }

    // Returns the bounding box of the LARGEST connected run of matching pixels. A plain bounding box
    // over every matching pixel merges the fixture surface with any unrelated pixel of a similar hue
    // elsewhere on the monitor, which yields a centre that is not on the surface at all.
    public static Rectangle FindColorBlock(byte[] pngBytes, Color color, int tolerance = 28)
    {
        using var stream = new MemoryStream(pngBytes, writable: false);
        using var bitmap = new Bitmap(stream);
        var width = bitmap.Width;
        var height = bitmap.Height;
        // Bitmap.GetPixel costs roughly a microsecond of GDI+ overhead per call. Over a 1600x900
        // preview that is 1.44 million calls, about ninety seconds per observation, which starves the
        // MCP client's read loop until it cancels. LockBits reads the whole buffer once instead.
        var matches = new bool[width * height];
        var data = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var stride = data.Stride;
            var buffer = new byte[stride * height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
            for (var y = 0; y < height; y++)
            {
                var row = y * stride;
                for (var x = 0; x < width; x++)
                {
                    // Format32bppArgb is laid out B, G, R, A in memory.
                    var offset = row + (x * 4);
                    matches[(y * width) + x] =
                        Math.Abs(buffer[offset + 2] - color.R) <= tolerance &&
                        Math.Abs(buffer[offset + 1] - color.G) <= tolerance &&
                        Math.Abs(buffer[offset] - color.B) <= tolerance;
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        var visited = new bool[matches.Length];
        var best = Rectangle.Empty;
        var bestArea = 0;
        var stack = new Stack<int>();
        for (var index = 0; index < matches.Length; index++)
        {
            if (!matches[index] || visited[index])
            {
                continue;
            }

            stack.Push(index);
            visited[index] = true;
            var minX = int.MaxValue;
            var minY = int.MaxValue;
            var maxX = int.MinValue;
            var maxY = int.MinValue;
            var area = 0;
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                var currentX = current % width;
                var currentY = current / width;
                area++;
                minX = Math.Min(minX, currentX);
                minY = Math.Min(minY, currentY);
                maxX = Math.Max(maxX, currentX);
                maxY = Math.Max(maxY, currentY);
                PushNeighbour(stack, matches, visited, width, height, currentX - 1, currentY);
                PushNeighbour(stack, matches, visited, width, height, currentX + 1, currentY);
                PushNeighbour(stack, matches, visited, width, height, currentX, currentY - 1);
                PushNeighbour(stack, matches, visited, width, height, currentX, currentY + 1);
            }

            if (area > bestArea)
            {
                bestArea = area;
                best = new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
            }
        }

        return bestArea < 64 ? Rectangle.Empty : best;
    }

    private static void PushNeighbour(
        Stack<int> stack,
        bool[] matches,
        bool[] visited,
        int width,
        int height,
        int x,
        int y)
    {
        if (x < 0 || y < 0 || x >= width || y >= height)
        {
            return;
        }

        var index = (y * width) + x;
        if (!matches[index] || visited[index])
        {
            return;
        }

        visited[index] = true;
        stack.Push(index);
    }

    // The MCP server runs under Pointframe's PerMonitorV2 manifest, so every desktop coordinate it
    // reports is a true physical pixel. A test host without a manifest is DPI-unaware and would read
    // GetCursorPos back in virtualized units, which silently compares two different coordinate
    // spaces. Opt this process into the same awareness before touching either.
    public static void EnsurePerMonitorDpiAwareness()
    {
        if (Interlocked.Exchange(ref _dpiAwarenessSet, 1) == 1)
        {
            return;
        }

        NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.PerMonitorAwareV2);
    }

    public static (int Width, int Height) DecodePngSize(byte[] pngBytes)
    {
        using var stream = new MemoryStream(pngBytes, writable: false);
        using var bitmap = new Bitmap(stream);
        return (bitmap.Width, bitmap.Height);
    }

    public static (int X, int Y) GetCursorPosition()
    {
        return NativeMethods.GetCursorPos(out var point)
            ? (point.X, point.Y)
            : throw new InvalidOperationException("The cursor position could not be read.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_sessionId is not null)
        {
            try
            {
                await _client.CallToolAsync(
                    "desktop_end_test_session",
                    new { sessionId = _sessionId, actionId = Guid.NewGuid().ToString() },
                    TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            }
            catch
            {
            }

            _sessionId = null;
        }

        await _client.DisposeAsync().ConfigureAwait(false);
        KillFixtureProcess(_fixtureProcessId);
        DesktopGatePolicyFactory.Delete(_policyPath);
        Environment.SetEnvironmentVariable("POINTFRAME_FIXTURE_STATE_PATH", null);
        Environment.SetEnvironmentVariable("POINTFRAME_FIXTURE_MONITOR", null);
    }

    private static void KillFixtureProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static async Task<int> WaitForFixtureAsync(HashSet<int> before, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var candidate = Process.GetProcessesByName("Pointframe.DesktopTestFixture")
                .FirstOrDefault(item => !before.Contains(item.Id));
            if (candidate is not null)
            {
                return candidate.Id;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException("The desktop test fixture process did not appear.");
    }

    private static async Task WaitForStateAsync(string statePath, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 150; attempt++)
        {
            if (File.Exists(statePath))
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                return;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException($"The fixture never wrote its state file: {statePath}");
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            public int X;
            public int Y;
        }

        internal static readonly nint PerMonitorAwareV2 = new(-4);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetProcessDpiAwarenessContext(nint context);
    }
}

public sealed record FixtureState(
    string LastEvent,
    int ClickCount,
    Point LastClickPoint,
    Size ClickTargetSize,
    Rectangle ClickTargetScreen,
    int DragDownCount,
    int DragMoveWhileDownCount,
    int DragUpCount,
    Point DragDownPoint,
    Point DragUpPoint,
    Rectangle DragSurfaceScreen,
    int WheelCount,
    int WheelDeltaTotal,
    Point LastWheelPoint,
    Rectangle ScrollSurfaceScreen,
    Rectangle TextBoxScreen,
    string TextBoxText,
    Rectangle WindowScreen,
    Point CursorScreen,
    string Raw)
{
    public static FixtureState Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return new FixtureState(
            root.GetProperty("lastEvent").GetString()!,
            root.GetProperty("clickCount").GetInt32(),
            ParsePoint(root, "lastClickPoint"),
            ParseSize(root, "clickTargetSize"),
            ParseRectangle(root, "clickTargetScreen"),
            root.GetProperty("dragDownCount").GetInt32(),
            root.GetProperty("dragMoveWhileDownCount").GetInt32(),
            root.GetProperty("dragUpCount").GetInt32(),
            ParsePoint(root, "dragDownPoint"),
            ParsePoint(root, "dragUpPoint"),
            ParseRectangle(root, "dragSurfaceScreen"),
            root.GetProperty("wheelCount").GetInt32(),
            root.GetProperty("wheelDeltaTotal").GetInt32(),
            ParsePoint(root, "lastWheelPoint"),
            ParseRectangle(root, "scrollSurfaceScreen"),
            ParseRectangle(root, "textBoxScreen"),
            root.GetProperty("textBoxText").GetString() ?? string.Empty,
            ParseRectangle(root, "windowScreen"),
            ParsePoint(root, "cursorScreen"),
            json);
    }

    private static Point ParsePoint(JsonElement root, string name)
    {
        var parts = root.GetProperty(name).GetString()!.Split(',');
        return new Point(int.Parse(parts[0]), int.Parse(parts[1]));
    }

    private static Size ParseSize(JsonElement root, string name)
    {
        var point = ParsePoint(root, name);
        return new Size(point.X, point.Y);
    }

    private static Rectangle ParseRectangle(JsonElement root, string name)
    {
        var parts = root.GetProperty(name).GetString()!.Split(',');
        return new Rectangle(
            int.Parse(parts[0]),
            int.Parse(parts[1]),
            int.Parse(parts[2]),
            int.Parse(parts[3]));
    }
}
