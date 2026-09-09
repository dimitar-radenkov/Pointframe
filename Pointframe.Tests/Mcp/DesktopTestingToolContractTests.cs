using System.Reflection;
using ModelContextProtocol.Server;
using Pointframe.Mcp;
using Xunit;

namespace Pointframe.Tests.Mcp;

public sealed class DesktopTestingToolContractTests
{
    [Fact]
    public void CatalogContainsExactlyFifteenGateATools()
    {
        Assert.Equal(15, PointframeCommandCatalog.DesktopTestingTools.Count);
        Assert.Equal(15, PointframeCommandCatalog.DesktopTestingTools.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void DirectToolsMatchKnownDeliveredToolIdentifiers()
    {
        // Mirrors the exact tool set asserted against the live server by packaging/test-mcp-stdio.ps1.
        string[] expected =
        [
            "list_displays",
            "list_windows",
            "capture_window",
            "read_text_from_window",
            "capture_monitor",
            "read_text_from_monitor",
            "start_recording",
            "stop_recording",
            "get_recording_status",
        ];

        Assert.Equal(expected, PointframeCommandCatalog.DirectTools);
    }

    [Fact]
    public void DesktopTestingToolsMatchKnownDeliveredToolIdentifiers()
    {
        // Mirrors the exact tool set asserted against the live server by packaging/test-mcp-stdio.ps1.
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            "list_apps", "start_test_session", "restart_app", "observe_app", "focus_window",
            "click", "press_keys", "drag", "enter_text", "invoke", "check_ui", "scroll",
            "get_action_result", "get_test_report", "end_test_session",
        };

        Assert.Equal(expected, PointframeCommandCatalog.DesktopTestingTools.ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public void ToolTypeExposesFifteenMethods()
    {
        var type = typeof(PointframeCommandCatalog).Assembly.GetType("Pointframe.Mcp.DesktopTestingMcpTools");
        Assert.NotNull(type);
        var methods = type!.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .ToArray();

        Assert.Equal(15, methods.Length);
    }

    [Fact]
    public void CatalogNamesMatchEveryRegisteredToolMethodOnBothToolTypes()
    {
        var directType = typeof(PointframeCommandCatalog).Assembly.GetType("Pointframe.Mcp.PointframeMcpTools");
        var desktopType = typeof(PointframeCommandCatalog).Assembly.GetType("Pointframe.Mcp.DesktopTestingMcpTools");
        Assert.NotNull(directType);
        Assert.NotNull(desktopType);

        Assert.Equal(CountToolMethods(directType!), PointframeCommandCatalog.DirectTools.Count);
        Assert.Equal(CountToolMethods(desktopType!), PointframeCommandCatalog.DesktopTestingTools.Count);
    }

    private static int CountToolMethods(Type type)
    {
        return type.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Count(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null);
    }
}
