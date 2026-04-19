using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class MicrophoneDeviceServiceTests
{
    [Fact]
    public void GetAvailableCaptureDeviceNames_ReturnsDistinctNonWhitespaceNames()
    {
        var service = CreateService();

        var names = service.GetAvailableCaptureDeviceNames();

        Assert.NotNull(names);
        Assert.DoesNotContain(names, name => string.IsNullOrWhiteSpace(name));
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void GetDefaultCaptureDeviceName_ReturnsNullOrTrimmedValue()
    {
        var service = CreateService();

        var name = service.GetDefaultCaptureDeviceName();

        if (name is not null)
        {
            Assert.Equal(name.Trim(), name);
        }
    }

    [Fact]
    public void TryGetCaptureDeviceMuted_WithEmptyName_ReturnsNull()
    {
        var service = CreateService();

        var result = service.TryGetCaptureDeviceMuted(string.Empty);

        Assert.Null(result);
    }

    [Fact]
    public void TrySetCaptureDeviceMuted_WithWhitespaceName_ReturnsFalse()
    {
        var service = CreateService();

        var result = service.TrySetCaptureDeviceMuted("   ", true);

        Assert.False(result);
    }

    [Fact]
    public void TryGetCaptureDeviceMuted_WithUnknownName_ReturnsNull()
    {
        var service = CreateService();

        var result = service.TryGetCaptureDeviceMuted($"missing-device-{Guid.NewGuid():N}");

        Assert.Null(result);
    }

    [Fact]
    public void TrySetCaptureDeviceMuted_WithUnknownName_ReturnsFalse()
    {
        var service = CreateService();

        var result = service.TrySetCaptureDeviceMuted($"missing-device-{Guid.NewGuid():N}", true);

        Assert.False(result);
    }

    [Fact]
    public void FindCaptureEndpoint_WithWhitespaceName_ReturnsNull()
    {
        var service = CreateService();

        var endpoint = InvokeFindCaptureEndpoint(service, " ");

        Assert.Null(endpoint);
    }

    private static MicrophoneDeviceService CreateService()
    {
        return new MicrophoneDeviceService(NullLogger<MicrophoneDeviceService>.Instance);
    }

    private static object? InvokeFindCaptureEndpoint(MicrophoneDeviceService service, string name)
    {
        var method = typeof(MicrophoneDeviceService).GetMethod("FindCaptureEndpoint", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method.Invoke(service, [name]);
    }
}
