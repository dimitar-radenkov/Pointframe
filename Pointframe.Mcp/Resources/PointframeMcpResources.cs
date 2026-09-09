using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Server;
using Pointframe.Engine;
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

    [McpServerResource(UriTemplate = "pointframe://server-info", Name = "Pointframe server info", MimeType = "application/json")]
    [Description("Returns the Pointframe MCP server version, whether desktop testing tools are enabled, and whether ffmpeg (required for recording) was found.")]
    public string GetServerInfo()
    {
        var ffmpegAvailability = FfmpegDirectVideoWriterFactory.GetAvailability();
        var response = new McpServerInfoResponse(
            SchemaVersion: 1,
            Version: GetVersion(),
            DesktopTestingEnabled: options.Enabled,
            Ffmpeg: new McpFfmpegAvailability(ffmpegAvailability.Found, ffmpegAvailability.Path, ffmpegAvailability.Source));
        return JsonSerializer.Serialize(response);
    }

    private static string GetVersion()
    {
        // Assembly.Location is empty for single-file publishes; use Environment.ProcessPath instead.
        var location = Environment.ProcessPath ?? Assembly.GetEntryAssembly()?.Location;
        if (!string.IsNullOrEmpty(location))
        {
            var fileVersionInfo = FileVersionInfo.GetVersionInfo(location);
            if (!string.IsNullOrWhiteSpace(fileVersionInfo.ProductVersion))
            {
                return fileVersionInfo.ProductVersion;
            }
        }

        var assemblyVersion = Assembly.GetEntryAssembly()?.GetName().Version;
        return assemblyVersion?.ToString() ?? "0.0.0";
    }
}
