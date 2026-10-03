using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pointframe.Cli;

internal sealed class VsCodeMcpConfigurator : IMcpClientConfigurator
{
    internal VsCodeMcpConfigurator(string configurationPath)
    {
        ConfigurationPath = configurationPath;
    }

    public string ConfigurationPath { get; }

    public void Configure(string executablePath, bool dryRun)
    {
        var root = ReadConfiguration();
        var servers = root["servers"] as JsonObject ?? [];
        root["servers"] = servers;
        servers["pointframe"] = new JsonObject
        {
            ["type"] = "stdio",
            ["command"] = executablePath,
        };

        if (dryRun)
        {
            return;
        }

        var directory = Path.GetDirectoryName(ConfigurationPath)
            ?? throw new InvalidOperationException("The VS Code MCP configuration path has no parent directory.");
        Directory.CreateDirectory(directory);
        if (File.Exists(ConfigurationPath))
        {
            File.Copy(ConfigurationPath, $"{ConfigurationPath}.pointframe.bak", overwrite: true);
        }

        var temporaryPath = $"{ConfigurationPath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporaryPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, ConfigurationPath, overwrite: true);
    }

    public bool IsConfiguredFor(string executablePath)
    {
        try
        {
            var root = ReadConfiguration();
            var configuredPath = root["servers"]?["pointframe"]?["command"]?.GetValue<string>();
            return string.Equals(configuredPath, executablePath, StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private JsonObject ReadConfiguration()
    {
        if (!File.Exists(ConfigurationPath))
        {
            return [];
        }

        return JsonNode.Parse(File.ReadAllText(ConfigurationPath)) as JsonObject
            ?? throw new JsonException("The VS Code MCP configuration root must be a JSON object.");
    }
}
