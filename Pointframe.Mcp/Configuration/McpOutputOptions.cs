namespace Pointframe.Mcp.Configuration;

/// <summary>
/// Server-wide output options for tool discovery and results.
/// </summary>
public sealed record McpOutputOptions(bool CompactText, bool FullDiscovery)
{
    public const string CompactTextFlag = "--compact-text";

    public const string FullDiscoveryFlag = "--full-discovery";

    public const string CompactTextEnvironmentVariable = "POINTFRAME_MCP_COMPACT_TEXT";

    public static McpOutputOptions Parse(IReadOnlyList<string> args, Func<string, string?>? getEnvironmentVariable = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        var environment = (getEnvironmentVariable ?? Environment.GetEnvironmentVariable)(CompactTextEnvironmentVariable);
        var fromEnvironment = environment is not null
            && (environment.Equals("1", StringComparison.Ordinal)
                || environment.Equals("true", StringComparison.OrdinalIgnoreCase)
                || environment.Equals("yes", StringComparison.OrdinalIgnoreCase));
        return new McpOutputOptions(
            fromEnvironment || args.Contains(CompactTextFlag, StringComparer.Ordinal),
            args.Contains(FullDiscoveryFlag, StringComparer.Ordinal));
    }
}
