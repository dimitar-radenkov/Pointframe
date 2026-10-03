using System.IO;
using System.Text.RegularExpressions;
using Pointframe.Mcp;
using Xunit;

namespace Pointframe.Tests.DocsSync;

/// <summary>
/// Guards <c>packaging/test-mcp-stdio.ps1</c>'s hand-maintained expected-tool arrays against drifting away
/// from <see cref="PointframeCommandCatalog"/>, which derives the real tool set reflectively from
/// <c>McpServerTool</c>-decorated methods. The packaging script itself also fails at release time if the
/// live server returns an unexpected tool set, but this test catches the same drift immediately in
/// <c>dotnet test</c>, without publishing and starting the MCP server.
/// </summary>
public sealed class McpPackagingScriptDocumentationTests
{
    [Fact]
    public void PackagingScript_ExpectedToolsMatchDisabledCatalog()
    {
        var script = ReadPackagingScript();
        var expectedTools = ExtractArray(script, "$expectedTools");

        AssertSameSet(PointframeCommandCatalog.DirectTools, expectedTools, "$expectedTools");
    }

    [Fact]
    public void PackagingScript_EnabledExpectedToolsMatchEnabledCatalog()
    {
        var script = ReadPackagingScript();
        var expectedTools = ExtractArray(script, "$expectedTools");
        var additionalEnabledTools = ExtractArray(script, "$enabledExpectedTools");

        var scriptEnabledTools = expectedTools.Concat(additionalEnabledTools).ToArray();
        AssertSameSet(PointframeCommandCatalog.Create(desktopTestingEnabled: true), scriptEnabledTools, "$enabledExpectedTools");
    }

    // Claude Desktop shows the MCPB manifest's tool list on install, so a stale list misdescribes the server.
    [Fact]
    public void BuildScript_ManifestToolsMatchDirectCatalog()
    {
        var script = File.ReadAllText(FindRepoFile(Path.Combine("packaging", "build-mcp-package.ps1")));
        var manifestTools = Regex.Matches(script, "@\\{\\s*name\\s*=\\s*\"(?<value>[^\"]+)\";\\s*description\\s*=")
            .Select(m => m.Groups["value"].Value)
            .ToArray();

        AssertSameSet(PointframeCommandCatalog.DirectTools, manifestTools, "manifest tools", "packaging/build-mcp-package.ps1");
    }

    private static void AssertSameSet(
        IReadOnlyList<string> actual,
        IReadOnlyList<string> expected,
        string variableName,
        string scriptPath = "packaging/test-mcp-stdio.ps1")
    {
        var missing = actual.Where(name => !expected.Contains(name)).ToArray();
        var stale = expected.Where(name => !actual.Contains(name)).ToArray();

        Assert.True(
            missing.Length == 0 && stale.Length == 0,
            $"{scriptPath}'s '{variableName}' is out of sync with the registered MCP tool catalog. "
            + $"Missing from script: {string.Join(", ", missing)}. Stale in script: {string.Join(", ", stale)}.");
    }

    /// <summary>
    /// Extracts the string literals from a PowerShell "$variableName = @(...)" or
    /// "$variableName = $other + @(...)" array literal.
    /// </summary>
    private static string[] ExtractArray(string script, string variableName)
    {
        var match = Regex.Match(script, $@"{Regex.Escape(variableName)}\s*=.*?@\((?<items>[^)]*)\)", RegexOptions.Singleline);
        Assert.True(match.Success, $"'{variableName}' was not found in packaging/test-mcp-stdio.ps1.");

        return Regex.Matches(match.Groups["items"].Value, "\"(?<value>[^\"]+)\"")
            .Select(m => m.Groups["value"].Value)
            .ToArray();
    }

    private static string ReadPackagingScript() =>
        File.ReadAllText(FindRepoFile(Path.Combine("packaging", "test-mcp-stdio.ps1")));

    private static string FindRepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"{relativePath} was not found in any parent directory.");
    }
}
