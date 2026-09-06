using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Pointframe.Mcp;

[McpServerResourceType]
internal sealed class PointframeMcpResources
{
    [McpServerResource(UriTemplate = "pointframe://commands", Name = "Pointframe commands", MimeType = "application/json")]
    [Description("Returns the available direct Pointframe MCP command identifiers.")]
    public static string GetCommands()
    {
        return "[\"displays.list\",\"capture.monitor\"]";
    }
}