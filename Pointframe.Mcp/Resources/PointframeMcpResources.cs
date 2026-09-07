using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Pointframe.Mcp.Configuration;

namespace Pointframe.Mcp;

[McpServerResourceType]
internal sealed class PointframeMcpResources(DesktopTestingHostOptions options)
{
    [McpServerResource(UriTemplate = "pointframe://commands", Name = "Pointframe commands", MimeType = "application/json")]
    [Description("Returns the available direct Pointframe MCP command identifiers.")]
    public string GetCommands()
    {
        return JsonSerializer.Serialize(PointframeCommandCatalog.Create(options.Enabled));
    }
}
