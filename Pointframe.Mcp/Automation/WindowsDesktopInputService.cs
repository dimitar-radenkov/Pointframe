using Pointframe.Engine;
using Pointframe.Engine.Automation.Models;

namespace Pointframe.Mcp.Automation;

public enum DesktopInputMethod
{
    Physical,
    GlobalHotkey,
}

public sealed record DesktopInputTarget(
    DesktopWindowIdentity? Window = null,
    DesktopSurfaceIdentity? Surface = null,
    PixelBounds? BoundsPixels = null);

public sealed record DesktopClickRequest(
    DesktopInputTarget Target,
    int X,
    int Y,
    int Count = 1,
    bool RightButton = false);

public sealed record DesktopKeyPressRequest(
    DesktopInputTarget? Target,
    IReadOnlyList<ushort> VirtualKeys,
    DesktopInputMethod Method = DesktopInputMethod.Physical,
    string? GlobalHotkeyId = null);

public sealed record DesktopDragRequest(
    DesktopInputTarget Target,
    IReadOnlyList<PixelBounds> Points,
    int DurationMilliseconds = 250);

public sealed record DesktopTextRequest(
    DesktopInputTarget Target,
    string Text,
    bool SemanticValue = false,
    int? X = null,
    int? Y = null);

public sealed record DesktopScrollRequest(
    DesktopInputTarget Target,
    int Detents,
    int? X = null,
    int? Y = null);

public sealed record DesktopInputPreflightResult(
    bool IsValid,
    string Code,
    string Message)
{
    public static DesktopInputPreflightResult Valid() => new(true, "Ok", "Input preflight passed.");

    public static DesktopInputPreflightResult Invalid(string code, string message) => new(false, code, message);
}

public interface IDesktopInputNativeAdapter
{
    nint GetForegroundWindow();

    bool IsForegroundForProcess(DesktopProcessIdentity expectedProcess)
    {
        return GetForegroundWindow() != nint.Zero;
    }

    bool SetForeground(nint handle);

    bool IsWindowValid(nint handle);

    bool IsWindowVisible(nint handle);

    bool IsWindowOwnedByProcess(nint handle, int processId)
    {
        return true;
    }

    bool IsPointVisible(PixelBounds bounds, int x, int y);

    bool SendClick(int x, int y, bool rightButton, int count);

    bool SendKeys(IReadOnlyList<ushort> virtualKeys);

    bool SendDrag(IReadOnlyList<PixelBounds> points, int durationMilliseconds);

    bool SendUnicodeText(string text);

    bool SendScroll(int? x, int? y, int detents);

    void ReleaseOwnedInput();

    bool TryReleaseOwnedInput()
    {
        ReleaseOwnedInput();
        return true;
    }
}

public interface IDesktopInputNativeAdapterFactory
{
    IDesktopInputNativeAdapter Create();
}

public interface IWindowsDesktopInputService
{
    Task<DesktopInputPreflightResult> FocusAsync(
        DesktopInputTarget target,
        DesktopProcessIdentity expectedProcess,
        CancellationToken cancellationToken = default);

    Task<DesktopInputPreflightResult> DragAsync(
        DesktopDragRequest request,
        DesktopProcessIdentity expectedProcess,
        CancellationToken cancellationToken = default);

    Task<DesktopInputPreflightResult> EnterTextAsync(
        DesktopTextRequest request,
        DesktopProcessIdentity expectedProcess,
        CancellationToken cancellationToken = default);

    Task<DesktopInputPreflightResult> ScrollAsync(
        DesktopScrollRequest request,
        DesktopProcessIdentity expectedProcess,
        CancellationToken cancellationToken = default);

    Task<DesktopInputPreflightResult> ClickAsync(
        DesktopClickRequest request,
        DesktopProcessIdentity expectedProcess,
        CancellationToken cancellationToken = default);

    Task<DesktopInputPreflightResult> PressKeysAsync(
        DesktopKeyPressRequest request,
        DesktopProcessIdentity expectedProcess,
        CancellationToken cancellationToken = default);

    void ReleaseOwnedInput();

    bool TryReleaseOwnedInput()
    {
        ReleaseOwnedInput();
        return true;
    }
}

public sealed class WindowsDesktopInputService : IWindowsDesktopInputService
{
    private readonly IDesktopInputNativeAdapter _native;

    public WindowsDesktopInputService(IDesktopInputNativeAdapterFactory? factory = null)
    {
        _native = (factory ?? new WindowsDesktopInputNativeAdapterFactory()).Create();
    }

    public Task<DesktopInputPreflightResult> FocusAsync(
        DesktopInputTarget target,
        DesktopProcessIdentity expectedProcess,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(expectedProcess);
        cancellationToken.ThrowIfCancellationRequested();
        var result = ValidateTarget(target, expectedProcess, requireForeground: false);
        if (!result.IsValid)
        {
            return Task.FromResult(result);
        }

        if (target.Window is null || !_native.IsWindowValid(target.Window.NativeHandle))
        {
            return Task.FromResult(DesktopInputPreflightResult.Invalid("WindowUnavailable", "The target window is no longer valid."));
        }

        if (!_native.SetForeground(target.Window.NativeHandle))
        {
            return Task.FromResult(DesktopInputPreflightResult.Invalid("FocusFailed", "The target window could not be focused."));
        }

        return Task.FromResult(_native.GetForegroundWindow() == target.Window.NativeHandle
            ? DesktopInputPreflightResult.Valid()
            : DesktopInputPreflightResult.Invalid("FocusFailed", "The target window did not become foreground."));
    }

    public async Task<DesktopInputPreflightResult> ClickAsync(
        DesktopClickRequest request,
        DesktopProcessIdentity expectedProcess,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(expectedProcess);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Count is < 1 or > DesktopTestingLimits.MaxClickCount)
        {
            return DesktopInputPreflightResult.Invalid("InvalidClickCount", "Click count must be one or two.");
        }

        var validation = ValidateTarget(request.Target, expectedProcess, requireForeground: true);
        if (!validation.IsValid)
        {
            return validation;
        }

        if (request.Target.BoundsPixels is not { } bounds || !_native.IsPointVisible(bounds, request.X, request.Y))
        {
            return DesktopInputPreflightResult.Invalid("OccludedOrOutOfBounds", "The click point is not visible within the approved target.");
        }

        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        return _native.SendClick(request.X, request.Y, request.RightButton, request.Count)
            ? DesktopInputPreflightResult.Valid()
            : DesktopInputPreflightResult.Invalid("InputDispatchFailed", "The native click was not accepted.");
    }

    public Task<DesktopInputPreflightResult> PressKeysAsync(
        DesktopKeyPressRequest request,
        DesktopProcessIdentity expectedProcess,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(expectedProcess);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.VirtualKeys is null || request.VirtualKeys.Count is < 1 or > DesktopTestingLimits.MaxSimultaneousKeys)
        {
            return Task.FromResult(DesktopInputPreflightResult.Invalid("InvalidKeyCount", "A key press must contain one to four keys."));
        }

        if (request.Method == DesktopInputMethod.GlobalHotkey && string.IsNullOrWhiteSpace(request.GlobalHotkeyId))
        {
            return Task.FromResult(DesktopInputPreflightResult.Invalid("GlobalHotkeyNotApproved", "A configured global hotkey ID is required."));
        }

        var validation = request.Method == DesktopInputMethod.GlobalHotkey
            ? DesktopInputPreflightResult.Valid()
            : ValidateTarget(request.Target ?? new DesktopInputTarget(), expectedProcess, requireForeground: true);
        if (!validation.IsValid)
        {
            return Task.FromResult(validation);
        }

        return Task.FromResult(_native.SendKeys(request.VirtualKeys)
            ? DesktopInputPreflightResult.Valid()
            : DesktopInputPreflightResult.Invalid("InputDispatchFailed", "The native key input was not accepted."));
    }

    public void ReleaseOwnedInput() => _ = TryReleaseOwnedInput();

    public bool TryReleaseOwnedInput() => _native.TryReleaseOwnedInput();

    public async Task<DesktopInputPreflightResult> DragAsync(
        DesktopDragRequest request,
        DesktopProcessIdentity expectedProcess,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(expectedProcess);
        if (request.Points.Count is < DesktopTestingLimits.MinDragPoints or > DesktopTestingLimits.MaxDragPoints)
        {
            return DesktopInputPreflightResult.Invalid("InvalidDragPoints", "A drag requires two to 128 points.");
        }

        if (request.DurationMilliseconds is < DesktopTestingLimits.MinDragDurationMilliseconds or > DesktopTestingLimits.MaxDragDurationMilliseconds)
        {
            return DesktopInputPreflightResult.Invalid("InvalidDragDuration", "Drag duration is outside the permitted range.");
        }

        var validation = ValidateTarget(request.Target, expectedProcess, requireForeground: true);
        if (!validation.IsValid)
        {
            return validation;
        }

        foreach (var point in request.Points)
        {
            if (request.Target.BoundsPixels is not { } bounds || !_native.IsPointVisible(bounds, point.X, point.Y))
            {
                return DesktopInputPreflightResult.Invalid("OccludedOrOutOfBounds", "A drag point is outside the approved target.");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return _native.SendDrag(request.Points, request.DurationMilliseconds)
            ? DesktopInputPreflightResult.Valid()
            : DesktopInputPreflightResult.Invalid("InputDispatchFailed", "The native drag was not accepted.");
    }

    public Task<DesktopInputPreflightResult> EnterTextAsync(
        DesktopTextRequest request,
        DesktopProcessIdentity expectedProcess,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(expectedProcess);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(request.Text) || request.Text.Length > DesktopTestingLimits.MaxTextLength)
        {
            return Task.FromResult(DesktopInputPreflightResult.Invalid("InvalidTextLength", "Text must be between one and 4096 characters."));
        }

        if (request.SemanticValue)
        {
            return Task.FromResult(DesktopInputPreflightResult.Invalid("UnsupportedSemanticValue", "Semantic ValuePattern entry requires a verified provider element."));
        }

        var validation = ValidateTarget(request.Target, expectedProcess, requireForeground: true);
        if (!validation.IsValid)
        {
            return Task.FromResult(validation);
        }

        if (request.X is { } x && request.Y is { } y)
        {
            if (request.Target.BoundsPixels is not { } textBounds || !_native.IsPointVisible(textBounds, x, y))
            {
                return Task.FromResult(DesktopInputPreflightResult.Invalid(
                    "OccludedOrOutOfBounds",
                    "The text target is not visible within the approved target."));
            }

            // Synthesized characters go to whatever currently holds keyboard focus, so the caret has
            // to be placed first. The tool has always documented that it clicks the point before
            // typing; it validated the point and then never clicked, so the text landed wherever
            // focus happened to be and the call still reported success.
            if (!_native.SendClick(x, y, rightButton: false, count: 1))
            {
                return Task.FromResult(DesktopInputPreflightResult.Invalid(
                    "InputDispatchFailed",
                    "The caret could not be placed before typing."));
            }

            // Give the target a moment to take focus; characters sent into a control that is still
            // activating are discarded.
            Thread.Sleep(WindowsDesktopInputNativeAdapter.FocusSettleMilliseconds);
        }

        return Task.FromResult(_native.SendUnicodeText(request.Text)
            ? DesktopInputPreflightResult.Valid()
            : DesktopInputPreflightResult.Invalid("InputDispatchFailed", "The native Unicode text input was not accepted."));
    }

    public Task<DesktopInputPreflightResult> ScrollAsync(
        DesktopScrollRequest request,
        DesktopProcessIdentity expectedProcess,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(expectedProcess);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Detents is < DesktopTestingLimits.MinScrollDetents or > DesktopTestingLimits.MaxScrollDetents || request.Detents == 0)
        {
            return Task.FromResult(DesktopInputPreflightResult.Invalid("InvalidScrollDetents", "Scroll detents must be nonzero and between -10 and 10."));
        }

        var validation = ValidateTarget(request.Target, expectedProcess, requireForeground: true);
        if (!validation.IsValid)
        {
            return Task.FromResult(validation);
        }

        if (request.X is { } x && request.Y is { } y &&
            (request.Target.BoundsPixels is not { } scrollBounds || !_native.IsPointVisible(scrollBounds, x, y)))
        {
            return Task.FromResult(DesktopInputPreflightResult.Invalid(
                "OccludedOrOutOfBounds",
                "The scroll target is not visible within the approved target."));
        }

        return Task.FromResult(_native.SendScroll(request.X, request.Y, request.Detents)
            ? DesktopInputPreflightResult.Valid()
            : DesktopInputPreflightResult.Invalid("InputDispatchFailed", "The native scroll was not accepted."));
    }

    private DesktopInputPreflightResult ValidateTarget(
        DesktopInputTarget target,
        DesktopProcessIdentity expectedProcess,
        bool requireForeground)
    {
        if (target.Window is null && target.Surface is null && target.BoundsPixels is null)
        {
            return DesktopInputPreflightResult.Invalid("TargetUnavailable", "A validated window, shell surface, or observation bounds are required.");
        }

        if (target.Window is not null)
        {
            if (!string.Equals(target.Window.ProcessRef, expectedProcess.ProcessRef, StringComparison.Ordinal))
            {
                return DesktopInputPreflightResult.Invalid("ProcessMismatch", "The target window belongs to a different process generation.");
            }

            if (!_native.IsWindowValid(target.Window.NativeHandle) || !_native.IsWindowVisible(target.Window.NativeHandle))
            {
                return DesktopInputPreflightResult.Invalid("WindowUnavailable", "The target window is unavailable or hidden.");
            }

            // The window ref's process segment is minted from the session's own process and never read
            // back off the handle, so a caller can keep a valid processRef while substituting any other
            // live HWND's hex suffix. Cross-check the handle's actual owning process id -- not just the
            // caller-supplied ref -- before anything is allowed to act on it.
            if (!_native.IsWindowOwnedByProcess(target.Window.NativeHandle, expectedProcess.ProcessId))
            {
                return DesktopInputPreflightResult.Invalid("WindowOwnerMismatch", "The target window handle does not belong to the approved process.");
            }

            if (requireForeground && _native.GetForegroundWindow() != target.Window.NativeHandle)
            {
                return DesktopInputPreflightResult.Invalid("FocusRequired", "The target window is not foreground.");
            }
        }
        else if (requireForeground && !_native.IsForegroundForProcess(expectedProcess))
        {
            return DesktopInputPreflightResult.Invalid("FocusRequired", "The approved process is not foreground.");
        }

        if (target.Surface is not null &&
            !string.Equals(target.Surface.OwnerProcessRef, expectedProcess.ProcessRef, StringComparison.Ordinal))
        {
            return DesktopInputPreflightResult.Invalid("SurfaceOwnerMismatch", "The shell surface is not owned by the approved process.");
        }

        return DesktopInputPreflightResult.Valid();
    }

}

public sealed class WindowsDesktopInputNativeAdapterFactory : IDesktopInputNativeAdapterFactory
{
    public IDesktopInputNativeAdapter Create() => new WindowsDesktopInputNativeAdapter();
}

internal sealed class WindowsDesktopInputNativeAdapter : IDesktopInputNativeAdapter
{
    internal const int MinDragMoveSteps = 5;

    private readonly HashSet<ushort> _ownedKeys = [];
    private readonly HashSet<uint> _ownedMouseButtons = [];

    public nint GetForegroundWindow() => WindowsDesktopNativeMethods.GetForegroundWindow();

    public bool IsForegroundForProcess(DesktopProcessIdentity expectedProcess)
    {
        var foreground = GetForegroundWindow();
        if (foreground == nint.Zero)
        {
            return false;
        }

        return WindowsDesktopNativeMethods.GetWindowThreadProcessId(foreground, out var processId) != 0
            && processId == expectedProcess.ProcessId;
    }

    public bool SetForeground(nint handle) => WindowsDesktopNativeMethods.SetForegroundWindow(handle);

    public bool IsWindowValid(nint handle) => handle != 0 && WindowsDesktopNativeMethods.IsWindow(handle);

    public bool IsWindowVisible(nint handle) => WindowsDesktopNativeMethods.IsWindowVisible(handle);

    public bool IsWindowOwnedByProcess(nint handle, int processId)
    {
        return WindowsDesktopNativeMethods.GetWindowThreadProcessId(handle, out var owningProcessId) != 0
            && owningProcessId == (uint)processId;
    }

    public bool IsPointVisible(PixelBounds bounds, int x, int y)
    {
        return x >= bounds.X && y >= bounds.Y && x < bounds.X + bounds.Width && y < bounds.Y + bounds.Height;
    }

    internal const int MoveSettleMilliseconds = 16;

    // The wheel needs a longer settle than a button press. A press is hit-tested where it lands, but
    // the wheel is routed to whichever window Windows currently considers to be under the cursor, and
    // that is recomputed on the target's own message pump rather than synchronously with the move.
    internal const int WheelSettleMilliseconds = 60;
    internal const int ButtonHoldMilliseconds = 50;
    internal const int ClickGapMilliseconds = 40;

    public bool SendClick(int x, int y, bool rightButton, int count)
    {
        var up = rightButton
            ? WindowsDesktopNativeMethods.MouseEventRightUp
            : WindowsDesktopNativeMethods.MouseEventLeftUp;
        var down = rightButton
            ? WindowsDesktopNativeMethods.MouseEventRightDown
            : WindowsDesktopNativeMethods.MouseEventLeftDown;

        foreach (var (flags, delay) in BuildClickSteps(rightButton, count))
        {
            if (!Send([Mouse(x, y, flags)]))
            {
                return false;
            }

            if (flags == down)
            {
                // Owned from press to release, so a failure mid-click cannot leave the button held.
                _ownedMouseButtons.Add(up);
            }
            else if (flags == up)
            {
                _ownedMouseButtons.Remove(up);
            }

            if (delay > 0)
            {
                Thread.Sleep(delay);
            }
        }

        return true;
    }

    internal static IReadOnlyList<(uint Flags, int DelayAfterMilliseconds)> BuildClickSteps(bool rightButton, int count)
    {
        // The pointer move is its own event, and the press and release are separate events with a real
        // hold between them. Batching press and release into one zero-delay SendInput call is discarded
        // as noise by many controls -- Scintilla among them -- so the call reports success and the
        // application does nothing. Real hardware never produces a 0 ms press.
        var down = rightButton
            ? WindowsDesktopNativeMethods.MouseEventRightDown
            : WindowsDesktopNativeMethods.MouseEventLeftDown;
        var up = rightButton
            ? WindowsDesktopNativeMethods.MouseEventRightUp
            : WindowsDesktopNativeMethods.MouseEventLeftUp;

        var steps = new List<(uint Flags, int DelayAfterMilliseconds)>
        {
            (0u, MoveSettleMilliseconds),
        };
        for (var index = 0; index < count; index++)
        {
            steps.Add((down, ButtonHoldMilliseconds));

            // The gap between releases stays well inside the system double-click time, so a count of
            // two still registers as a double click rather than two unrelated clicks.
            steps.Add((up, index == count - 1 ? 0 : ClickGapMilliseconds));
        }

        return steps;
    }

    public bool SendKeys(IReadOnlyList<ushort> virtualKeys)
    {
        var releasableKeys = virtualKeys
            .Where(key => !IsKeyDown(key))
            .Distinct()
            .ToArray();

        var inputs = virtualKeys
            .Select(key => new WindowsDesktopNativeMethods.INPUT
            {
                Type = WindowsDesktopNativeMethods.InputKeyboard,
                Data = new WindowsDesktopNativeMethods.InputUnion
                {
                    Keyboard = KeyDown(key),
                },
            })
            .Concat(virtualKeys.Reverse().Select(key => new WindowsDesktopNativeMethods.INPUT
            {
                Type = WindowsDesktopNativeMethods.InputKeyboard,
                Data = new WindowsDesktopNativeMethods.InputUnion
                {
                    Keyboard = KeyUp(key),
                },
            }))
            .ToArray();
        var sent = WindowsDesktopNativeMethods.SendInput(
            (uint)inputs.Length,
            inputs,
            System.Runtime.InteropServices.Marshal.SizeOf<WindowsDesktopNativeMethods.INPUT>()) == inputs.Length;
        if (sent)
        {
            foreach (var key in releasableKeys)
            {
                _ownedKeys.Add(key);
            }
        }

        return sent;
    }

    // Extended keys must carry KEYEVENTF_EXTENDEDKEY or they arrive as their numpad twins: an
    // agent pressing Home or an arrow key otherwise types a digit into the target.
    private static readonly HashSet<ushort> ExtendedVirtualKeys =
    [
        0x2D, 0x2E, 0x24, 0x23, 0x21, 0x22, 0x25, 0x26, 0x27, 0x28,
        0xA3, 0xA5, 0x90, 0x6F, 0x5D, 0x5B, 0x5C,
    ];

    private static WindowsDesktopNativeMethods.KEYBDINPUT KeyDown(ushort virtualKey) =>
        Key(virtualKey, 0);

    private static WindowsDesktopNativeMethods.KEYBDINPUT KeyUp(ushort virtualKey) =>
        Key(virtualKey, WindowsDesktopNativeMethods.KeyEventKeyUp);

    private static WindowsDesktopNativeMethods.KEYBDINPUT Key(ushort virtualKey, uint flags)
    {
        if (ExtendedVirtualKeys.Contains(virtualKey))
        {
            flags |= WindowsDesktopNativeMethods.KeyEventExtendedKey;
        }

        return new WindowsDesktopNativeMethods.KEYBDINPUT
        {
            Vk = virtualKey,
            Scan = (ushort)WindowsDesktopNativeMethods.MapVirtualKey(virtualKey, 0),
            DwFlags = flags,
        };
    }

    private static bool IsKeyDown(ushort virtualKey) =>
        (WindowsDesktopNativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    public bool SendDrag(IReadOnlyList<PixelBounds> points, int durationMilliseconds)
    {
        // A drag is not two clicks. The button must stay down across a path of intermediate moves,
        // or the target sees a press and a release at two unrelated points and never starts dragging.
        var path = BuildDragPath(points);
        if (!Send([Mouse(path[0].X, path[0].Y, 0)]))
        {
            return false;
        }

        // The pointer move and the press are separate events, and the press is delivered to whatever
        // sits under the cursor at the moment it arrives. Without a settle the press can land at the
        // previous position -- after another gesture that is a different control entirely, and the
        // drag silently never starts.
        Thread.Sleep(MoveSettleMilliseconds);
        if (!Send([Mouse(path[0].X, path[0].Y, WindowsDesktopNativeMethods.MouseEventLeftDown)]))
        {
            return false;
        }

        _ownedMouseButtons.Add(WindowsDesktopNativeMethods.MouseEventLeftUp);
        var stepDelay = Math.Max(1, durationMilliseconds / Math.Max(1, path.Count - 1));
        for (var index = 1; index < path.Count; index++)
        {
            Thread.Sleep(stepDelay);
            if (!Send([Mouse(path[index].X, path[index].Y, 0)]))
            {
                // Leave the button owned: TryReleaseOwnedInput is what keeps a failed drag from
                // leaving the user with a physically stuck mouse button.
                return false;
            }
        }

        if (!Send([Mouse(path[^1].X, path[^1].Y, WindowsDesktopNativeMethods.MouseEventLeftUp)]))
        {
            return false;
        }

        _ownedMouseButtons.Remove(WindowsDesktopNativeMethods.MouseEventLeftUp);
        return true;
    }

    internal static IReadOnlyList<PixelBounds> BuildDragPath(IReadOnlyList<PixelBounds> points)
    {
        if (points.Count > MinDragMoveSteps)
        {
            return points;
        }

        // Most drag sources need several move events to recognise a drag gesture at all; a single
        // jump from the press point to the release point is routinely ignored.
        var segments = points.Count - 1;
        var stepsPerSegment = (int)Math.Ceiling(MinDragMoveSteps / (double)segments);
        var path = new List<PixelBounds> { points[0] };
        for (var index = 0; index < segments; index++)
        {
            var from = points[index];
            var to = points[index + 1];
            for (var step = 1; step <= stepsPerSegment; step++)
            {
                var ratio = step / (double)stepsPerSegment;
                path.Add(new PixelBounds(
                    from.X + (int)Math.Round((to.X - from.X) * ratio),
                    from.Y + (int)Math.Round((to.Y - from.Y) * ratio),
                    1,
                    1));
            }
        }

        return path;
    }

    internal const int TextChunkSize = 4;
    internal const int TextChunkDelayMilliseconds = 12;
    internal const int FocusSettleMilliseconds = 60;

    public bool SendUnicodeText(string text)
    {
        // Pace the characters. Pushing the whole string as one SendInput batch is accepted by Windows
        // and reported as success, but a target's message pump drops the tail: a sixteen character
        // string arrived as eight. Chunking with a short gap matches what a real keyboard produces.
        foreach (var chunk in Chunk(text, TextChunkSize))
        {
            if (!Send(BuildUnicodeInputs(chunk)))
            {
                return false;
            }

            Thread.Sleep(TextChunkDelayMilliseconds);
        }

        return true;
    }

    internal static IEnumerable<string> Chunk(string text, int size)
    {
        for (var index = 0; index < text.Length; index += size)
        {
            yield return text.Substring(index, Math.Min(size, text.Length - index));
        }
    }

    private static WindowsDesktopNativeMethods.INPUT[] BuildUnicodeInputs(string text)
    {
        var inputs = text.SelectMany(character =>
        {
            var code = (ushort)character;
            return new[]
            {
                new WindowsDesktopNativeMethods.INPUT
                {
                    Type = WindowsDesktopNativeMethods.InputKeyboard,
                    Data = new WindowsDesktopNativeMethods.InputUnion
                    {
                        Keyboard = new WindowsDesktopNativeMethods.KEYBDINPUT { Scan = code, DwFlags = WindowsDesktopNativeMethods.KeyEventUnicode },
                    },
                },
                new WindowsDesktopNativeMethods.INPUT
                {
                    Type = WindowsDesktopNativeMethods.InputKeyboard,
                    Data = new WindowsDesktopNativeMethods.InputUnion
                    {
                        Keyboard = new WindowsDesktopNativeMethods.KEYBDINPUT { Scan = code, DwFlags = WindowsDesktopNativeMethods.KeyEventUnicode | WindowsDesktopNativeMethods.KeyEventKeyUp },
                    },
                },
            };
        }).ToArray();
        return inputs;
    }

    public bool SendScroll(int? x, int? y, int detents)
    {
        // The wheel goes to whatever sits under the cursor, not to the focused window, so a scroll
        // request carrying a point must move the cursor there first or it scrolls the wrong control.
        if (x is { } scrollX && y is { } scrollY)
        {
            if (!Send([Mouse(scrollX, scrollY, 0)]))
            {
                return false;
            }

            // Same hazard as the drag press: the wheel goes to whatever is under the cursor when it
            // arrives, so it has to arrive after the move has taken effect. Scrolling straight after
            // another gesture otherwise turns the wheel over the previous target.
            Thread.Sleep(WheelSettleMilliseconds);
        }

        var input = new WindowsDesktopNativeMethods.INPUT
        {
            Type = WindowsDesktopNativeMethods.InputMouse,
            Data = new WindowsDesktopNativeMethods.InputUnion
            {
                Mouse = new WindowsDesktopNativeMethods.MOUSEINPUT { MouseData = (uint)(detents * 120), DwFlags = WindowsDesktopNativeMethods.MouseEventWheel },
            },
        };
        return Send([input]);
    }

    public void ReleaseOwnedInput() => _ = TryReleaseOwnedInput();

    public bool TryReleaseOwnedInput()
    {
        // The mouse button goes first: a stuck left button is far more disruptive to the user than a
        // stuck modifier, and a failed key release must not skip it.
        var released = ReleaseOwnedMouseButtons();
        if (_ownedKeys.Count == 0)
        {
            return released;
        }

        var inputs = _ownedKeys.Select(key => new WindowsDesktopNativeMethods.INPUT
        {
            Type = WindowsDesktopNativeMethods.InputKeyboard,
            Data = new WindowsDesktopNativeMethods.InputUnion
            {
                Keyboard = KeyUp(key),
            },
        }).ToArray();
        var sent = WindowsDesktopNativeMethods.SendInput(
            (uint)inputs.Length,
            inputs,
            System.Runtime.InteropServices.Marshal.SizeOf<WindowsDesktopNativeMethods.INPUT>());
        if (sent != inputs.Length)
        {
            return false;
        }

        _ownedKeys.Clear();
        return released;
    }

    private bool ReleaseOwnedMouseButtons()
    {
        if (_ownedMouseButtons.Count == 0)
        {
            return true;
        }

        var inputs = _ownedMouseButtons
            .Select(flag => new WindowsDesktopNativeMethods.INPUT
            {
                Type = WindowsDesktopNativeMethods.InputMouse,
                Data = new WindowsDesktopNativeMethods.InputUnion
                {
                    Mouse = new WindowsDesktopNativeMethods.MOUSEINPUT { DwFlags = flag },
                },
            })
            .ToArray();
        if (!Send(inputs))
        {
            return false;
        }

        _ownedMouseButtons.Clear();
        return true;
    }

    internal static (int X, int Y) NormalizeAbsolute(int x, int y, PixelBounds virtualScreen)
    {
        // Absolute mouse coordinates are normalized 0-65535 across the *virtual* screen, which is why
        // the caller must also set MouseEventVirtualDesk; without that flag Windows reinterprets these
        // same numbers against the primary monitor and every secondary-monitor click misses.
        var normalizedX = Math.Clamp((int)Math.Round((x - virtualScreen.X) * 65535d / Math.Max(1, virtualScreen.Width - 1)), 0, 65535);
        var normalizedY = Math.Clamp((int)Math.Round((y - virtualScreen.Y) * 65535d / Math.Max(1, virtualScreen.Height - 1)), 0, 65535);
        return (normalizedX, normalizedY);
    }

    private static PixelBounds GetVirtualScreen() => new(
        WindowsDesktopNativeMethods.GetSystemMetrics(WindowsDesktopNativeMethods.VirtualScreenX),
        WindowsDesktopNativeMethods.GetSystemMetrics(WindowsDesktopNativeMethods.VirtualScreenY),
        Math.Max(1, WindowsDesktopNativeMethods.GetSystemMetrics(WindowsDesktopNativeMethods.VirtualScreenWidth)),
        Math.Max(1, WindowsDesktopNativeMethods.GetSystemMetrics(WindowsDesktopNativeMethods.VirtualScreenHeight)));

    private static WindowsDesktopNativeMethods.INPUT Mouse(int x, int y, uint flags)
    {
        var (normalizedX, normalizedY) = NormalizeAbsolute(x, y, GetVirtualScreen());
        return new WindowsDesktopNativeMethods.INPUT
        {
            Type = WindowsDesktopNativeMethods.InputMouse,
            Data = new WindowsDesktopNativeMethods.InputUnion
            {
                Mouse = new WindowsDesktopNativeMethods.MOUSEINPUT
                {
                    Dx = normalizedX,
                    Dy = normalizedY,
                    DwFlags = flags
                        | WindowsDesktopNativeMethods.MouseEventMove
                        | WindowsDesktopNativeMethods.MouseEventAbsolute
                        | WindowsDesktopNativeMethods.MouseEventVirtualDesk,
                },
            },
        };
    }

    private static bool Send(WindowsDesktopNativeMethods.INPUT[] inputs) =>
        WindowsDesktopNativeMethods.SendInput(
            (uint)inputs.Length,
            inputs,
            System.Runtime.InteropServices.Marshal.SizeOf<WindowsDesktopNativeMethods.INPUT>()) == inputs.Length;
}
