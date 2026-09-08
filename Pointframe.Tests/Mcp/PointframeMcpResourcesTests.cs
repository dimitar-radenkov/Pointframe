using System.Reflection;
using System.Text.Json;
using Pointframe.Mcp;
using Pointframe.Mcp.Configuration;
using Xunit;

namespace Pointframe.Tests.Mcp;

public sealed class PointframeMcpResourcesTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GetServerInfo_ReturnsVersionDesktopTestingFlagAndFfmpegAvailability(bool desktopTestingEnabled)
    {
        var resourcesType = typeof(PointframeCommandCatalog).Assembly.GetType("Pointframe.Mcp.PointframeMcpResources");
        Assert.NotNull(resourcesType);

        var options = new DesktopTestingHostOptions(desktopTestingEnabled, false, null, null, null);
        var instance = Activator.CreateInstance(resourcesType!, options);
        var method = resourcesType!.GetMethod("GetServerInfo", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(method);

        var json = (string)method!.Invoke(instance, null)!;
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("SchemaVersion").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("Version").GetString()));
        Assert.Equal(desktopTestingEnabled, root.GetProperty("DesktopTestingEnabled").GetBoolean());

        var ffmpeg = root.GetProperty("Ffmpeg");
        Assert.False(string.IsNullOrWhiteSpace(ffmpeg.GetProperty("Path").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(ffmpeg.GetProperty("Source").GetString()));
    }

    [Fact]
    public void GetCommands_StillReturnsToolCatalog()
    {
        // Regression guard: confirms adding the server-info resource did not disturb the existing commands resource.
        var resourcesType = typeof(PointframeCommandCatalog).Assembly.GetType("Pointframe.Mcp.PointframeMcpResources");
        Assert.NotNull(resourcesType);

        var options = new DesktopTestingHostOptions(false, false, null, null, null);
        var instance = Activator.CreateInstance(resourcesType!, options);
        var method = resourcesType!.GetMethod("GetCommands", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(method);

        var json = (string)method!.Invoke(instance, null)!;
        var tools = JsonSerializer.Deserialize<string[]>(json);

        Assert.Equal(PointframeCommandCatalog.DirectTools, tools);
    }
}
