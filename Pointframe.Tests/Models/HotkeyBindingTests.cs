using System.Windows.Input;
using Pointframe.Models;
using Xunit;

namespace Pointframe.Tests.Models;

public sealed class HotkeyBindingTests
{
    [Fact]
    public void DisplayName_WhenKeyNotSet_ReturnsNotSet()
    {
        Assert.Equal("Not set", new HotkeyBinding(0, HotkeyModifiers.Ctrl).DisplayName);
    }

    [Fact]
    public void DisplayName_ComposesModifiersInCtrlShiftAltOrder()
    {
        var binding = new HotkeyBinding(0x53, HotkeyModifiers.Ctrl | HotkeyModifiers.Shift | HotkeyModifiers.Alt); // S

        Assert.Equal("Ctrl+Shift+Alt+S", binding.DisplayName);
    }

    [Fact]
    public void DisplayName_ForPrintScreen_UsesFriendlyName()
    {
        Assert.Equal("Print Screen", new HotkeyBinding(0x2C, HotkeyModifiers.None).DisplayName);
    }

    [Fact]
    public void Matches_WhenKeyAndModifiersMatch_ReturnsTrue()
    {
        var binding = new HotkeyBinding(0x53, HotkeyModifiers.Ctrl); // S

        Assert.True(binding.Matches(Key.S, ModifierKeys.Control));
    }

    [Fact]
    public void Matches_WhenModifiersDiffer_ReturnsFalse()
    {
        var binding = new HotkeyBinding(0x53, HotkeyModifiers.Ctrl); // S

        Assert.False(binding.Matches(Key.S, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.False(binding.Matches(Key.S, ModifierKeys.None));
    }

    [Fact]
    public void Matches_WhenKeyDiffers_ReturnsFalse()
    {
        var binding = new HotkeyBinding(0x53, HotkeyModifiers.Ctrl); // S

        Assert.False(binding.Matches(Key.A, ModifierKeys.Control));
    }

    [Fact]
    public void Matches_WhenKeyNotSet_NeverMatches()
    {
        var binding = new HotkeyBinding(0, HotkeyModifiers.None);

        Assert.False(binding.Matches(Key.None, ModifierKeys.None));
    }
}
