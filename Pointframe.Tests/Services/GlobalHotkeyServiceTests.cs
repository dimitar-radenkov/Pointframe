using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Pointframe.Models;
using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class GlobalHotkeyServiceTests
{
    private static GlobalHotkeyService CreateService(UserSettings? settings = null)
    {
        var mock = new Mock<IUserSettingsService>();
        mock.SetupGet(s => s.Current).Returns(settings ?? new UserSettings());
        return new GlobalHotkeyService(mock.Object, NullLogger<GlobalHotkeyService>.Instance);
    }

    [Fact]
    public void BeginKeyCaptureMode_StoresCallback()
    {
        var svc = CreateService();
        Action<uint, HotkeyModifiers> callback = (_, _) => { };

        svc.BeginKeyCaptureMode(callback);

        var field = typeof(GlobalHotkeyService)
            .GetField("_keyCaptureCallback", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        Assert.Same(callback, field!.GetValue(svc));
    }

    [Fact]
    public void EndKeyCaptureMode_ClearsCallback()
    {
        var svc = CreateService();
        svc.BeginKeyCaptureMode((_, _) => { });

        svc.EndKeyCaptureMode();

        var field = typeof(GlobalHotkeyService)
            .GetField("_keyCaptureCallback", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        Assert.Null(field!.GetValue(svc));
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes_DoesNotThrow()
    {
        var svc = CreateService();
        svc.Dispose();
        var ex = Record.Exception(svc.Dispose);
        Assert.Null(ex);
    }

    [Theory]
    [InlineData(false, false, false, HotkeyModifiers.None, true)]
    [InlineData(true, false, false, HotkeyModifiers.Ctrl, true)]
    [InlineData(true, true, false, HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, true)]
    [InlineData(true, true, true, HotkeyModifiers.Ctrl | HotkeyModifiers.Shift | HotkeyModifiers.Alt, true)]
    [InlineData(true, false, false, HotkeyModifiers.None, false)]
    [InlineData(false, false, false, HotkeyModifiers.Ctrl, false)]
    [InlineData(true, true, false, HotkeyModifiers.Ctrl, false)]
    public void ModifiersMatch_ReturnsExpected(bool ctrl, bool shift, bool alt, HotkeyModifiers required, bool expected)
    {
        var method = typeof(GlobalHotkeyService)
            .GetMethod("ModifiersMatch", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = (bool)method!.Invoke(null, [required, ctrl, shift, alt])!;

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(NativeMethods.VK_SHIFT, true)]
    [InlineData(NativeMethods.VK_LSHIFT, true)]
    [InlineData(NativeMethods.VK_RSHIFT, true)]
    [InlineData(NativeMethods.VK_LCONTROL, true)]
    [InlineData(NativeMethods.VK_RCONTROL, true)]
    [InlineData(NativeMethods.VK_LMENU, true)]
    [InlineData(NativeMethods.VK_RMENU, true)]
    [InlineData(NativeMethods.VK_LWIN, true)]
    [InlineData(NativeMethods.VK_RWIN, true)]
    [InlineData(0x41u, false)] // 'A' — not a modifier
    [InlineData(NativeMethods.VK_ESCAPE, false)]
    public void IsModifierVk_ReturnsExpected(uint vk, bool expected)
    {
        var method = typeof(GlobalHotkeyService)
            .GetMethod("IsModifierVk", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = (bool)method!.Invoke(null, [vk])!;

        Assert.Equal(expected, result);
    }

    [Fact]
    public void CleanWindowSnipRequested_Event_CanBeSubscribedAndInvoked()
    {
        var svc = CreateService();
        var raised = false;
        svc.CleanWindowSnipRequested += () => raised = true;

        var eventField = typeof(GlobalHotkeyService).GetField("CleanWindowSnipRequested", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(eventField);

        var handler = (Action?)eventField!.GetValue(svc);
        handler?.Invoke();

        Assert.True(raised);
    }
}
