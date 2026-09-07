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

    bool IsPointVisible(PixelBounds bounds, int x, int y);

    bool SendClick(int x, int y, bool rightButton, int count);

    bool SendKeys(IReadOnlyList<ushort> virtualKeys);

    bool SendDrag(IReadOnlyList<PixelBounds> points, int durationMilliseconds);

    bool SendUnicodeText(string text);

    bool SendScroll(int detents);

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

        if (request.X is { } x && request.Y is { } y &&
            (request.Target.BoundsPixels is not { } textBounds || !_native.IsPointVisible(textBounds, x, y)))
        {
            return Task.FromResult(DesktopInputPreflightResult.Invalid(
                "OccludedOrOutOfBounds",
                "The text target is not visible within the approved target."));
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

        return Task.FromResult(_native.SendScroll(request.Detents)
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
    private readonly HashSet<ushort> _ownedKeys = [];

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

    public bool IsPointVisible(PixelBounds bounds, int x, int y)
    {
        return x >= bounds.X && y >= bounds.Y && x < bounds.X + bounds.Width && y < bounds.Y + bounds.Height;
    }

    public bool SendClick(int x, int y, bool rightButton, int count)
    {
        var inputs = new List<WindowsDesktopNativeMethods.INPUT>();
        for (var index = 0; index < count; index++)
        {
            inputs.Add(Mouse(x, y, rightButton ? WindowsDesktopNativeMethods.MouseEventRightDown : WindowsDesktopNativeMethods.MouseEventLeftDown));
            inputs.Add(Mouse(x, y, rightButton ? WindowsDesktopNativeMethods.MouseEventRightUp : WindowsDesktopNativeMethods.MouseEventLeftUp));
        }

        return WindowsDesktopNativeMethods.SendInput(
            (uint)inputs.Count,
            inputs.ToArray(),
            System.Runtime.InteropServices.Marshal.SizeOf<WindowsDesktopNativeMethods.INPUT>()) == inputs.Count;
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
                    Keyboard = new WindowsDesktopNativeMethods.KEYBDINPUT { Vk = key },
                },
            })
            .Concat(virtualKeys.Reverse().Select(key => new WindowsDesktopNativeMethods.INPUT
            {
                Type = WindowsDesktopNativeMethods.InputKeyboard,
                Data = new WindowsDesktopNativeMethods.InputUnion
                {
                    Keyboard = new WindowsDesktopNativeMethods.KEYBDINPUT { Vk = key, DwFlags = WindowsDesktopNativeMethods.KeyEventKeyUp },
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

    private static bool IsKeyDown(ushort virtualKey) =>
        (WindowsDesktopNativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    public bool SendDrag(IReadOnlyList<PixelBounds> points, int durationMilliseconds) =>
        SendClick(points[0].X, points[0].Y, false, 1) &&
        SendClick(points[^1].X, points[^1].Y, false, 1);

    public bool SendUnicodeText(string text)
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
        return WindowsDesktopNativeMethods.SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<WindowsDesktopNativeMethods.INPUT>()) == inputs.Length;
    }

    public bool SendScroll(int detents)
    {
        var input = new WindowsDesktopNativeMethods.INPUT
        {
            Type = WindowsDesktopNativeMethods.InputMouse,
            Data = new WindowsDesktopNativeMethods.InputUnion
            {
                Mouse = new WindowsDesktopNativeMethods.MOUSEINPUT { MouseData = (uint)(detents * 120), DwFlags = WindowsDesktopNativeMethods.MouseEventWheel },
            },
        };
        return WindowsDesktopNativeMethods.SendInput(1, [input], System.Runtime.InteropServices.Marshal.SizeOf<WindowsDesktopNativeMethods.INPUT>()) == 1;
    }

    public void ReleaseOwnedInput() => _ = TryReleaseOwnedInput();

    public bool TryReleaseOwnedInput()
    {
        if (_ownedKeys.Count == 0)
        {
            return true;
        }

        var inputs = _ownedKeys.Select(key => new WindowsDesktopNativeMethods.INPUT
        {
            Type = WindowsDesktopNativeMethods.InputKeyboard,
            Data = new WindowsDesktopNativeMethods.InputUnion
            {
                Keyboard = new WindowsDesktopNativeMethods.KEYBDINPUT
                {
                    Vk = key,
                    DwFlags = WindowsDesktopNativeMethods.KeyEventKeyUp,
                },
            },
        }).ToArray();
        var released = WindowsDesktopNativeMethods.SendInput(
            (uint)inputs.Length,
            inputs,
            System.Runtime.InteropServices.Marshal.SizeOf<WindowsDesktopNativeMethods.INPUT>());
        if (released == inputs.Length)
        {
            _ownedKeys.Clear();
            return true;
        }

        return false;
    }

    private static WindowsDesktopNativeMethods.INPUT Mouse(int x, int y, uint flags)
    {
        var originX = WindowsDesktopNativeMethods.GetSystemMetrics(WindowsDesktopNativeMethods.VirtualScreenX);
        var originY = WindowsDesktopNativeMethods.GetSystemMetrics(WindowsDesktopNativeMethods.VirtualScreenY);
        var width = Math.Max(1, WindowsDesktopNativeMethods.GetSystemMetrics(WindowsDesktopNativeMethods.VirtualScreenWidth));
        var height = Math.Max(1, WindowsDesktopNativeMethods.GetSystemMetrics(WindowsDesktopNativeMethods.VirtualScreenHeight));
        var normalizedX = Math.Clamp((int)Math.Round((x - originX) * 65535d / Math.Max(1, width - 1)), 0, 65535);
        var normalizedY = Math.Clamp((int)Math.Round((y - originY) * 65535d / Math.Max(1, height - 1)), 0, 65535);
        return new WindowsDesktopNativeMethods.INPUT
        {
            Type = WindowsDesktopNativeMethods.InputMouse,
            Data = new WindowsDesktopNativeMethods.InputUnion
            {
                Mouse = new WindowsDesktopNativeMethods.MOUSEINPUT
                {
                    Dx = normalizedX,
                    Dy = normalizedY,
                    DwFlags = flags | WindowsDesktopNativeMethods.MouseEventMove | WindowsDesktopNativeMethods.MouseEventAbsolute,
                },
            },
        };
    }
}
