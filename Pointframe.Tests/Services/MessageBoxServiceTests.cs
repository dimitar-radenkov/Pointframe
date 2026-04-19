using System.Reflection;
using System.Windows;
using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class MessageBoxServiceTests
{
    [Fact]
    public void MapButton_MapsYesNoAndDefaultsToOk()
    {
        Assert.Equal(0x00000004u, InvokeMapButton(MessageBoxButton.YesNo));
        Assert.Equal(0x00000000u, InvokeMapButton(MessageBoxButton.OK));
    }

    [Fact]
    public void MapImage_MapsErrorWarningAndDefaultsToInformation()
    {
        Assert.Equal(0x00000010u, InvokeMapImage(MessageBoxImage.Error));
        Assert.Equal(0x00000030u, InvokeMapImage(MessageBoxImage.Warning));
        Assert.Equal(0x00000040u, InvokeMapImage(MessageBoxImage.Information));
    }

    private static Window? InvokeGetOwnerWindow()
    {
        var method = typeof(MessageBoxService).GetMethod("GetOwnerWindow", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (Window?)method.Invoke(null, null);
    }

    private static uint InvokeMapButton(MessageBoxButton button)
    {
        var method = typeof(MessageBoxService).GetMethod("MapButton", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (uint)method.Invoke(null, [button])!;
    }

    private static uint InvokeMapImage(MessageBoxImage image)
    {
        var method = typeof(MessageBoxService).GetMethod("MapImage", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (uint)method.Invoke(null, [image])!;
    }
}