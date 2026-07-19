using System.Windows.Input;

namespace Pointframe.Models;

public readonly record struct HotkeyBinding(uint Key, HotkeyModifiers Modifiers)
{
    private const uint VkSnapshot = 0x2C; // KeyInterop maps VK_SNAPSHOT to "Snapshot", not the key's engraved name

    public string DisplayName
    {
        get
        {
            if (Key == 0)
            {
                return "Not set";
            }

            var parts = new List<string>();
            if (Modifiers.HasFlag(HotkeyModifiers.Ctrl))
            {
                parts.Add("Ctrl");
            }

            if (Modifiers.HasFlag(HotkeyModifiers.Shift))
            {
                parts.Add("Shift");
            }

            if (Modifiers.HasFlag(HotkeyModifiers.Alt))
            {
                parts.Add("Alt");
            }

            parts.Add(Key == VkSnapshot ? "Print Screen" : KeyInterop.KeyFromVirtualKey((int)Key).ToString());
            return string.Join("+", parts);
        }
    }
}
