using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using WpfApplication = System.Windows.Application;

namespace Pointframe.Services;

internal sealed class GlobalHotkeyService : IGlobalHotkeyService
{
    private readonly IUserSettingsService _userSettings;
    private readonly ILogger<GlobalHotkeyService> _logger;

    private NativeMethods.LowLevelKeyboardProc? _keyboardProc;
    private IntPtr _keyboardHook = IntPtr.Zero;
    private bool _disposed;

    private volatile Action<uint, HotkeyModifiers>? _keyCaptureCallback;

    public event Action? RegionSnipRequested;
    public event Action? WholeScreenSnipRequested;
    public event Action? WholeScreenRecordRequested;

    public GlobalHotkeyService(IUserSettingsService userSettings, ILogger<GlobalHotkeyService> logger)
    {
        _userSettings = userSettings;
        _logger = logger;
    }

    public void Register()
    {
        _keyboardProc = HookCallback;
        using var process = Process.GetCurrentProcess();
        var hMod = NativeMethods.GetModuleHandle(process.MainModule?.ModuleName);
        _keyboardHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _keyboardProc, hMod, 0);
        if (_keyboardHook == IntPtr.Zero)
        {
            _logger.LogWarning(
                "Failed to register low-level keyboard hook (error {Code}); Print Screen hotkey will not work",
                Marshal.GetLastWin32Error());
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
        {
            return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        var captureCallback = _keyCaptureCallback;
        if (captureCallback != null)
        {
            if (wParam == (IntPtr)NativeMethods.WM_KEYDOWN || wParam == (IntPtr)NativeMethods.WM_SYSKEYDOWN)
            {
                var kb = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                if (!IsModifierVk(kb.vkCode))
                {
                    var ctrlHeld = NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) < 0;
                    var shiftHeld = NativeMethods.GetAsyncKeyState(NativeMethods.VK_SHIFT) < 0;
                    var altHeld = NativeMethods.GetAsyncKeyState(NativeMethods.VK_MENU) < 0;
                    var modifiers = HotkeyModifiers.None;
                    if (ctrlHeld)
                    {
                        modifiers |= HotkeyModifiers.Ctrl;
                    }

                    if (shiftHeld)
                    {
                        modifiers |= HotkeyModifiers.Shift;
                    }

                    if (altHeld)
                    {
                        modifiers |= HotkeyModifiers.Alt;
                    }

                    WpfApplication.Current.Dispatcher.InvokeAsync(() => captureCallback(kb.vkCode, modifiers));
                    return (IntPtr)1; // block key from OS
                }
            }

            return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        if (wParam == (IntPtr)NativeMethods.WM_KEYDOWN || wParam == (IntPtr)NativeMethods.WM_SYSKEYDOWN)
        {
            var kb = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            var shiftHeld = NativeMethods.GetAsyncKeyState(NativeMethods.VK_SHIFT) < 0;
            var ctrlHeld = NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) < 0;
            var altHeld = NativeMethods.GetAsyncKeyState(NativeMethods.VK_MENU) < 0;

            var recordHotkey = _userSettings.Current.WholeScreenRecordHotkey;
            var recordModifiers = _userSettings.Current.WholeScreenRecordHotkeyModifiers;
            if (recordHotkey != 0 && kb.vkCode == recordHotkey && ModifiersMatch(recordModifiers, ctrlHeld, shiftHeld, altHeld))
            {
                WpfApplication.Current.Dispatcher.InvokeAsync(() => WholeScreenRecordRequested?.Invoke());
                return (IntPtr)1;
            }

            var regionHotkey = _userSettings.Current.RegionCaptureHotkey;
            var regionModifiers = _userSettings.Current.RegionCaptureHotkeyModifiers;
            if (regionHotkey != 0 && kb.vkCode == regionHotkey)
            {
                if (ModifiersMatch(regionModifiers, ctrlHeld, shiftHeld, altHeld))
                {
                    WpfApplication.Current.Dispatcher.InvokeAsync(() => RegionSnipRequested?.Invoke());
                    return (IntPtr)1;
                }
                else if (regionModifiers == HotkeyModifiers.None && !ctrlHeld && shiftHeld && !altHeld)
                {
                    WpfApplication.Current.Dispatcher.InvokeAsync(() => WholeScreenSnipRequested?.Invoke());
                    return (IntPtr)1;
                }
            }
        }

        return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private static bool ModifiersMatch(HotkeyModifiers required, bool ctrl, bool shift, bool alt) =>
        ctrl == required.HasFlag(HotkeyModifiers.Ctrl) &&
        shift == required.HasFlag(HotkeyModifiers.Shift) &&
        alt == required.HasFlag(HotkeyModifiers.Alt);

    private static bool IsModifierVk(uint vk) => vk is
        (uint)NativeMethods.VK_SHIFT or NativeMethods.VK_LSHIFT or NativeMethods.VK_RSHIFT or
        (uint)NativeMethods.VK_CONTROL or NativeMethods.VK_LCONTROL or NativeMethods.VK_RCONTROL or
        (uint)NativeMethods.VK_MENU or NativeMethods.VK_LMENU or NativeMethods.VK_RMENU or
        NativeMethods.VK_LWIN or NativeMethods.VK_RWIN;

    public void BeginKeyCaptureMode(Action<uint, HotkeyModifiers> onKeyPressed) => _keyCaptureCallback = onKeyPressed;

    public void EndKeyCaptureMode() => _keyCaptureCallback = null;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_keyboardHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
    }
}
