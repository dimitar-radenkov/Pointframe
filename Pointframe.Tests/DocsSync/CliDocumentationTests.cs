using System.IO;
using System.Text.RegularExpressions;
using Pointframe.Cli;
using Xunit;

namespace Pointframe.Tests.DocsSync;

/// <summary>
/// Guards against docs/cli/README.md drifting away from the CLI's own <see cref="CliCommandParser.HelpText"/>,
/// which is the single source of truth for supported commands and flags (also asserted against by
/// <c>CliApplicationTests.RunAsync_Help_WritesHelpTextAndExitsZero</c>). A new command or flag added to the
/// parser without a matching docs update fails this test instead of silently going undocumented.
/// </summary>
public sealed class CliDocumentationTests
{
    [Fact]
    public void CliReadme_DocumentsEveryCommandFromHelpText()
    {
        var readme = File.ReadAllText(FindRepoFile(Path.Combine("docs", "cli", "README.md")));

        var undocumented = ExtractCommandNames(CliCommandParser.HelpText)
            .Where(command => !Regex.IsMatch(readme, $@"\b{Regex.Escape(command)}\b"))
            .ToArray();

        Assert.True(
            undocumented.Length == 0,
            $"docs/cli/README.md does not mention these CLI commands: {string.Join(", ", undocumented)}");
    }

    [Fact]
    public void CliReadme_DocumentsEveryLongFlagFromHelpText()
    {
        var readme = File.ReadAllText(FindRepoFile(Path.Combine("docs", "cli", "README.md")));

        var undocumented = ExtractLongFlags(CliCommandParser.HelpText)
            .Where(flag => !readme.Contains(flag, StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            undocumented.Length == 0,
            $"docs/cli/README.md does not mention these CLI flags: {string.Join(", ", undocumented)}");
    }

    private static IEnumerable<string> ExtractCommandNames(string helpText)
    {
        var commandsBlock = FindParagraph(helpText, "Commands:");
        return Regex.Matches(commandsBlock, @"^\s*(?<name>[a-z]+)\s", RegexOptions.Multiline)
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal);
    }

    private static IEnumerable<string> ExtractLongFlags(string helpText)
    {
        var optionsBlock = FindParagraph(helpText, "Options:");
        return Regex.Matches(optionsBlock, @"(?<flag>--[a-z-]+)")
            .Select(match => match.Groups["flag"].Value)
            .Distinct(StringComparer.Ordinal);
    }

    /// <summary>
    /// Returns the blank-line-delimited paragraph that starts with the given heading line,
    /// tolerating either "\n" or "\r\n" line endings.
    /// </summary>
    private static string FindParagraph(string content, string heading)
    {
        var paragraphs = Regex.Split(content, @"\r?\n\s*\r?\n");
        var paragraph = paragraphs.FirstOrDefault(candidate => candidate.TrimStart().StartsWith(heading, StringComparison.Ordinal));

        Assert.True(paragraph is not null, $"'{heading}' heading was not found in CliCommandParser.HelpText.");
        var headingIndex = paragraph!.IndexOf(heading, StringComparison.Ordinal);
        return paragraph[(headingIndex + heading.Length)..];
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
