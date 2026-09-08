using System.Reflection;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

namespace Pointframe.Mcp;

public static partial class PointframeCommandCatalog
{
    public static readonly IReadOnlyList<string> DirectTools = GetToolNames(typeof(PointframeMcpTools));

    public static readonly IReadOnlyList<string> DesktopTestingTools = GetToolNames(typeof(DesktopTestingMcpTools));

    public static IReadOnlyList<string> Create(bool desktopTestingEnabled)
    {
        return desktopTestingEnabled
            ? DirectTools.Concat(DesktopTestingTools).ToArray()
            : DirectTools;
    }

    /// <summary>
    /// Derives MCP tool identifiers directly from a tool type's <see cref="McpServerToolAttribute"/>-decorated
    /// methods instead of a hand-maintained list, so a new tool method cannot silently drift out of sync with
    /// the resource that advertises available commands.
    /// </summary>
    private static IReadOnlyList<string> GetToolNames(Type toolType)
    {
        return toolType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Select(method => (method, attribute: method.GetCustomAttribute<McpServerToolAttribute>()))
            .Where(pair => pair.attribute is not null)
            .OrderBy(pair => pair.method.MetadataToken)
            .Select(pair => string.IsNullOrWhiteSpace(pair.attribute!.Name) ? ToToolName(pair.method.Name) : pair.attribute.Name)
            .ToArray();
    }

    private static string ToToolName(string methodName)
    {
        var withoutAsyncSuffix = methodName.EndsWith("Async", StringComparison.Ordinal)
            ? methodName[..^"Async".Length]
            : methodName;
        return PascalCaseBoundaryPattern().Replace(withoutAsyncSuffix, "_").ToLowerInvariant();
    }

    [GeneratedRegex("(?<!^)(?=[A-Z])")]
    private static partial Regex PascalCaseBoundaryPattern();
}
