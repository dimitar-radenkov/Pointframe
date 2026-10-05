namespace Pointframe.Mcp.Configuration;

/// <summary>
/// Server-wide output options. CompactText replaces the duplicated JSON text block of status-style tools
/// with a short summary; it is off unless the host passes --compact-text or sets POINTFRAME_MCP_COMPACT_TEXT.
/// </summary>
public sealed record McpOutputOptions(bool CompactText)
{
    public const string CompactTextFlag = "--compact-text";

    public const string CompactTextEnvironmentVariable = "POINTFRAME_MCP_COMPACT_TEXT";

    public static McpOutputOptions Parse(IReadOnlyList<string> args, Func<string, string?>? getEnvironmentVariable = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        var environment = (getEnvironmentVariable ?? Environment.GetEnvironmentVariable)(CompactTextEnvironmentVariable);
        var fromEnvironment = environment is not null
            && (environment.Equals("1", StringComparison.Ordinal)
                || environment.Equals("true", StringComparison.OrdinalIgnoreCase)
                || environment.Equals("yes", StringComparison.OrdinalIgnoreCase));
        return new McpOutputOptions(fromEnvironment || args.Contains(CompactTextFlag, StringComparer.Ordinal));
    }
}
