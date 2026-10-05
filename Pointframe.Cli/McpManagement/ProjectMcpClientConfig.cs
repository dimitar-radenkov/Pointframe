using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pointframe.Cli;

internal sealed record ProjectConfigResult(string Client, string RelativePath, bool Changed);

// Project-scoped agent configuration that runs `pointframe mcp serve`. It is merged, never replaced: other
// servers and settings stay, the one Pointframe entry is created or corrected, and a file that already
// holds the right entry is not touched. The first rewrite of a file keeps a .pointframe.bak copy.
internal static partial class ProjectMcpClientConfig
{
    internal const string ClaudeCode = "claude-code";
    internal const string Codex = "codex";
    internal const string VsCode = "vscode";
    internal const string ServerName = "pointframe";
    internal const string Command = "pointframe";

    internal static IReadOnlyList<string> Expand(string client) => client == "all" ? [ClaudeCode, Codex, VsCode] : [client];

    internal static string RelativePathOf(string client) => client switch
    {
        ClaudeCode => ".mcp.json",
        Codex => ".codex/config.toml",
        VsCode => ".vscode/mcp.json",
        _ => throw new ArgumentException($"Unsupported client '{client}'.", nameof(client)),
    };

    internal static ProjectConfigResult Write(string client, string projectRoot)
    {
        var relativePath = RelativePathOf(client);
        var path = Path.Combine(projectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var changed = client == Codex ? WriteToml(path) : WriteJson(path, client);
        return new ProjectConfigResult(client, relativePath, changed);
    }

    private static bool WriteJson(string path, string client)
    {
        var containerKey = client == VsCode ? "servers" : "mcpServers";
        var root = VsCodeMcpConfigurator.ReadJsonObject(path);
        var args = new JsonArray("mcp", "serve");
        var entry = new JsonObject();
        if (client == VsCode)
        {
            entry["type"] = "stdio";
        }

        entry["command"] = Command;
        if (client == VsCode)
        {
            args.Add("--project");
            args.Add("${workspaceFolder}");
        }

        entry["args"] = args;

        JsonObject servers;
        switch (root[containerKey])
        {
            case JsonObject existing:
                servers = existing;
                break;
            case null:
                servers = [];
                root[containerKey] = servers;
                break;
            default:
                throw new JsonException($"'{containerKey}' in {path} must be a JSON object.");
        }

        if (JsonNode.DeepEquals(servers[ServerName], entry))
        {
            return false;
        }

        servers[ServerName] = entry;
        WriteWithBackup(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        return true;
    }

    private static bool WriteToml(string path)
    {
        var original = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        var merged = MergeToml(original);
        if (merged == original)
        {
            return false;
        }

        WriteWithBackup(path, merged);
        return true;
    }

    internal static string MergeToml(string original)
    {
        var newline = original.Length == 0 ? Environment.NewLine : original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = original.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        string[] desired = ["command = \"pointframe\"", "args = [\"mcp\", \"serve\"]"];

        var header = lines.FindIndex(line => TableHeader().IsMatch(line));
        if (header < 0)
        {
            while (lines.Count > 0 && lines[^1].Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }

            if (lines.Count > 0)
            {
                lines.Add(string.Empty);
            }

            lines.Add("[mcp_servers.pointframe]");
            lines.AddRange(desired);
            lines.Add(string.Empty);
            return string.Join(newline, lines);
        }

        var end = lines.FindIndex(header + 1, line => line.TrimStart().StartsWith('['));
        if (end < 0)
        {
            end = lines.Count;
        }

        var commandLine = lines.FindIndex(header + 1, end - header - 1, line => KeyLine("command").IsMatch(line));
        if (commandLine >= 0)
        {
            lines[commandLine] = desired[0];
        }

        var argsLine = lines.FindIndex(header + 1, end - header - 1, line => KeyLine("args").IsMatch(line));
        if (argsLine >= 0)
        {
            var last = argsLine;
            if (lines[argsLine].Contains('[') && !lines[argsLine].Contains(']'))
            {
                while (last + 1 < end && !lines[last].Contains(']'))
                {
                    last++;
                }
            }

            lines.RemoveRange(argsLine, last - argsLine + 1);
            lines.Insert(argsLine, desired[1]);
        }
        else
        {
            lines.Insert(header + 1, desired[1]);
        }

        if (commandLine < 0)
        {
            lines.Insert(header + 1, desired[0]);
        }

        return string.Join(newline, lines);
    }

    private static Regex KeyLine(string key) => new($@"^\s*{key}\s*=", RegexOptions.CultureInvariant);

    [GeneratedRegex(@"^\s*\[\s*mcp_servers\s*\.\s*(pointframe|""pointframe"")\s*\]\s*(#.*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex TableHeader();

    private static void WriteWithBackup(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var backupPath = $"{path}.pointframe.bak";
        if (File.Exists(path) && !File.Exists(backupPath))
        {
            File.Copy(path, backupPath);
        }

        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporaryPath, contents, new UTF8Encoding(false));
        File.Move(temporaryPath, path, overwrite: true);
    }
}
