namespace Pointframe.Services;

public interface IGlobalHotkeyService : IDisposable
{
    event Action RegionSnipRequested;

    event Action WholeScreenSnipRequested;

    event Action WholeScreenRecordRequested;

    void Register();

    /// <summary>
    /// Starts key-capture mode. Non-modifier key presses are blocked at the OS level
    /// and routed to <paramref name="onKeyPressed"/> instead of firing snip/record events.
    /// Modifier keys (Ctrl/Shift/Alt) pass through so WPF can update the live display.
    /// Call <see cref="EndKeyCaptureMode"/> when done.
    /// </summary>
    void BeginKeyCaptureMode(Action<uint, HotkeyModifiers> onKeyPressed);

    void EndKeyCaptureMode();
}
