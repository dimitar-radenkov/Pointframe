using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    [InlineData("expectFailure")]
    [InlineData("criterionId")]
    public void GuideCoversTheVerificationWorkflow(string term)
    {
        Assert.Contains($"`{term}", _guide, StringComparison.Ordinal);
    }

    [Fact]
    public void PolicySampleIsAcceptedByTheLoader()
    {
        var sample = Regex.Match(_guide, "```json\\s*(\\{.*?\\})\\s*```", RegexOptions.Singleline).Groups[1].Value;
        var executable = Path.Combine(_root, "MyApp.exe");
        File.WriteAllBytes(executable, [0]);
        var policy = sample
            .Replace(@"C:\\absolute\\existing\\folder", JsonEncoded(_root), StringComparison.Ordinal)
            .Replace(@"C:\\absolute\\path\\MyApp.exe", JsonEncoded(executable), StringComparison.Ordinal)
            .Replace(@"C:\\absolute\\path", JsonEncoded(_root), StringComparison.Ordinal);
        var policyPath = Path.Combine(_root, "policy.json");
        File.WriteAllText(policyPath, policy);

        var loaded = new DesktopTestingPolicyLoader().LoadAndValidate(policyPath);

        Assert.Equal("my-app", Assert.Single(loaded.Profiles).Id);
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
