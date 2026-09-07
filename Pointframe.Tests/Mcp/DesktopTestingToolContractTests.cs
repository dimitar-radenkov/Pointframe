using System.Reflection;
using ModelContextProtocol.Server;
using Pointframe.Mcp;
using Xunit;

namespace Pointframe.Tests.Mcp;

public sealed class DesktopTestingToolContractTests
{
    [Fact]
    public void CatalogContainsExactlyElevenGateATools()
    {
        Assert.Equal(15, PointframeCommandCatalog.DesktopTestingTools.Count);
        Assert.Equal(15, PointframeCommandCatalog.DesktopTestingTools.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ToolTypeExposesElevenMethods()
    {
        var type = typeof(PointframeCommandCatalog).Assembly.GetType("Pointframe.Mcp.DesktopTestingMcpTools");
        Assert.NotNull(type);
        var methods = type!.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .ToArray();

        Assert.Equal(15, methods.Length);
    }
}
