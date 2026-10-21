using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pointframe.Cli;
using Pointframe.Mcp;
using Pointframe.Mcp.Configuration;
using Xunit;

namespace Pointframe.Tests.Mcp;

// The guide is what an outside agent follows to verify its own desktop work, so a tool name, parameter, or
// policy sample that drifted from the server would send every such agent down a failing path.
public sealed class VerifyDesktopWorkGuideTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pointframe-guide-{Guid.NewGuid():N}");
    private readonly string _guide = PointframeMcpResources.ReadVerifyDesktopWorkGuide();

    public VerifyDesktopWorkGuideTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void EveryToolTheGuideNamesExists()
    {
        var known = PointframeCommandCatalog.DirectTools.Concat(PointframeCommandCatalog.DesktopTestingTools).ToHashSet(StringComparer.Ordinal);
        var named = Regex.Matches(_guide, @"`([a-z]+(?:_[a-z]+)+)`").Select(match => match.Groups[1].Value)
            .Where(name => name.StartsWith("desktop_", StringComparison.Ordinal) || name.StartsWith("list_", StringComparison.Ordinal))
            .Distinct()
            .ToArray();

        Assert.NotEmpty(named);
        Assert.DoesNotContain(named, name => !known.Contains(name));
    }

    [Theory]
    [InlineData("desktop_start_test_session")]
    [InlineData("desktop_check_ui")]
    [InlineData("desktop_get_test_report")]
    [InlineData("desktop_end_test_session")]
    [InlineData("desktop_replay_checks")]
    [InlineData("desktop_export_scenario")]
    [InlineData("expectFailure")]
    [InlineData("criterionId")]
    public void GuideCoversTheVerificationWorkflow(string term)
    {
        Assert.Contains($"`{term}", _guide, StringComparison.Ordinal);
    }

    [Fact]
    public void GuideTeachesSelectorsBeforeObservation()
    {
        var selectorPath = _guide.IndexOf("**Use selectors first.**", StringComparison.Ordinal);
        var observationPath = _guide.IndexOf("Use `desktop_observe_app` to explore an unknown UI", StringComparison.Ordinal);

        Assert.True(selectorPath >= 0);
        Assert.True(observationPath > selectorPath);
        Assert.Contains("`semanticValue: true`", _guide, StringComparison.Ordinal);
        Assert.Contains("`automationId`", _guide, StringComparison.Ordinal);
        Assert.Contains("`chord: \"Ctrl+S\"`", _guide, StringComparison.Ordinal);
        Assert.Contains("`InspectionIncomplete`", _guide, StringComparison.Ordinal);
        Assert.Contains("`AmbiguousTarget`", _guide, StringComparison.Ordinal);
    }

    [Fact]
    public void SpecSampleIsAcceptedByTheLoader()
    {
        var sample = Regex.Match(_guide, @"```json\s*(\{.*?\})\s*```", RegexOptions.Singleline).Groups[1].Value;
        var specPath = Path.Combine(_root, ".pointframe", "verify.json");
        Directory.CreateDirectory(Path.GetDirectoryName(specPath)!);
        File.WriteAllText(specPath, sample);

        var spec = VerificationSpecLoader.Load(specPath);

        Assert.Equal("my-app", spec.App!.Id);
        Assert.Equal("save-text", Assert.Single(spec.Scenarios).Id);
    }

    [Theory]
    [InlineData("pointframe verify run")]
    [InlineData("pointframe verify setup")]
    [InlineData("pointframe verify trust")]
    [InlineData("`ListItem`")]
    [InlineData("`list item` never matches")]
    [InlineData("## If you are generating a verify-<app> skill")]
    public void GuideTeachesTheSpecFirstPath(string term)
    {
        Assert.Contains(term, _guide, StringComparison.Ordinal);
    }

    [Fact]
    public void GuideNeverAsksForAHandWrittenPolicy()
    {
        Assert.DoesNotContain("allowedActions", _guide, StringComparison.Ordinal);
        Assert.DoesNotContain("--desktop-policy", _guide, StringComparison.Ordinal);
        Assert.DoesNotContain("--force", _guide, StringComparison.Ordinal);
    }

    [Fact]
    public void ServerInfoPropertyTheGuideNamesIsSerializedThatWay()
    {
        var serverInfo = new PointframeMcpResources(new DesktopTestingHostOptions(Enabled: true, WorkerMode: false, PolicyPath: null, WorkerPipeName: null, ParentProcessId: null)).GetServerInfo();

        Assert.True(JsonDocument.Parse(serverInfo).RootElement.TryGetProperty("DesktopTestingEnabled", out _));
        Assert.Contains("`DesktopTestingEnabled`", _guide, StringComparison.Ordinal);
    }

    private static string JsonEncoded(string value) => JsonSerializer.Serialize(value)[1..^1];
}
