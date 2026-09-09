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
            "desktop_list_apps", "desktop_start_test_session", "desktop_restart_app",
            "desktop_observe_app", "desktop_focus_window", "desktop_click", "desktop_press_keys",
            "desktop_drag", "desktop_enter_text", "desktop_invoke", "desktop_check_ui",
            "desktop_scroll", "desktop_get_action_result", "desktop_get_test_report",
            "desktop_end_test_session",
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
