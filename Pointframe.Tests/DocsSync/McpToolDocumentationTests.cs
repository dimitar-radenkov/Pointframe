using System.IO;
using Pointframe.Mcp;
using Xunit;

namespace Pointframe.Tests.DocsSync;

/// <summary>
/// Guards against MCP tool names silently drifting away from the documentation that lists them.
/// <see cref="PointframeCommandCatalog"/> derives tool names reflectively from the
/// <c>McpServerTool</c>-decorated methods, so a newly added or renamed tool method automatically
/// changes what these tests check for without needing a parallel hand-maintained list here.
/// </summary>
public sealed class McpToolDocumentationTests
{
    private const string DirectToolsHeading = "### MCP capabilities";
    private const string DesktopTestingToolsHeading = "## Available opt-in tools";

    [Fact]
    public void Readme_DocumentsEveryDirectTool()
    {
        var section = ReadSection(FindRepoFile("README.md"), DirectToolsHeading, "\n### ");

        var undocumented = PointframeCommandCatalog.DirectTools
            .Where(name => !section.Contains($"`{name}`", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            undocumented.Length == 0,
            $"README.md '{DirectToolsHeading}' section is missing these registered MCP tools: {string.Join(", ", undocumented)}");
    }

    [Fact]
    public void DesktopTestingReadme_DocumentsEveryDesktopTestingTool()
    {
        var section = ReadSection(
            FindRepoFile(Path.Combine("docs", "mcp-desktop-testing", "README.md")),
            DesktopTestingToolsHeading,
            "\n## ");

        var undocumented = PointframeCommandCatalog.DesktopTestingTools
            .Where(name => !section.Contains($"`{name}`", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            undocumented.Length == 0,
            $"docs/mcp-desktop-testing/README.md '{DesktopTestingToolsHeading}' section is missing these registered tools: {string.Join(", ", undocumented)}");
    }

    private static string ReadSection(string filePath, string heading, string nextHeadingPrefix)
    {
        var content = File.ReadAllText(filePath);

        var start = content.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{heading}' heading was not found in {filePath}.");

        var afterHeading = start + heading.Length;
        var end = content.IndexOf(nextHeadingPrefix, afterHeading, StringComparison.Ordinal);
        return end < 0 ? content[afterHeading..] : content[afterHeading..end];
    }

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
