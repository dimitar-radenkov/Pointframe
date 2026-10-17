using System.IO;
using System.Text.RegularExpressions;
using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class TelemetryDocumentationTests
{
    private const string SectionHeading = "### What is collected";

    [Fact]
    public void PrivacyDocument_DocumentsEveryCatalogEvent()
    {
        var documented = ParseDocumentedEvents().Keys;
        var undocumented = TelemetryEventCatalog.All
            .Select(definition => definition.Name)
            .Where(name => !documented.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            undocumented.Length == 0,
            $"Telemetry events missing from the PRIVACY.md table: {string.Join(", ", undocumented)}");
    }

    [Fact]
    public void PrivacyDocument_DoesNotDocumentUnknownEvents()
    {
        var stale = ParseDocumentedEvents().Keys
            .Where(name => !TelemetryEventCatalog.TryGetDefinition(name, out _))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            stale.Length == 0,
            $"PRIVACY.md table documents events that no longer exist in the catalog: {string.Join(", ", stale)}");
    }

    [Fact]
    public void PrivacyDocument_DocumentsRequiredPropertiesOfEveryEvent()
    {
        var documented = ParseDocumentedEvents();

        foreach (var definition in TelemetryEventCatalog.All)
        {
            if (!documented.TryGetValue(definition.Name, out var properties))
            {
                continue;
            }

            foreach (var required in definition.RequiredProperties)
            {
                Assert.True(
                    properties.Contains($"`{required}`", StringComparison.Ordinal),
                    $"PRIVACY.md row for '{definition.Name}' does not document required property '{required}'.");
            }
        }
    }

    [Fact]
    public void PrivacyDocument_DocumentsActualHeartbeatInterval()
    {
        var documented = ParseDocumentedEvents();
        var hours = (int)TelemetryHeartbeatService.HeartbeatInterval.TotalHours;

        Assert.True(
            documented[TelemetryEvents.AppHeartbeat].Contains($"every {hours} hours", StringComparison.Ordinal),
            $"PRIVACY.md must state the real heartbeat interval ({TelemetryHeartbeatService.HeartbeatInterval}).");
    }

    private static Dictionary<string, string> ParseDocumentedEvents()
    {
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in ReadCollectedSection().Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith('|'))
            {
                continue;
            }

            var cells = trimmed.Trim('|').Split('|');
            if (cells.Length < 2)
            {
                continue;
            }

            // Header and separator rows carry no backticked event name.
            var name = Regex.Match(cells[0], "`([a-z0-9_]+)`");
            if (!name.Success)
            {
                continue;
            }

            rows[name.Groups[1].Value] = cells[1];
        }

        Assert.NotEmpty(rows);
        return rows;
    }

    private static string ReadCollectedSection()
    {
        var documentPath = FindPrivacyDocument();
        var content = File.ReadAllText(documentPath);

        var start = content.IndexOf(SectionHeading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{SectionHeading}' heading was not found in {documentPath}.");

        var afterHeading = start + SectionHeading.Length;
        var end = content.IndexOf("\n### ", afterHeading, StringComparison.Ordinal);
        return end < 0 ? content[afterHeading..] : content[afterHeading..end];
    }

    private static string FindPrivacyDocument()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "PRIVACY.md");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("PRIVACY.md was not found in any parent directory.");
    }
}
