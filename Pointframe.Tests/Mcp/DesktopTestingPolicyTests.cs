using System.IO;
using System.Text.Json;
using Pointframe.Engine.Automation.Models;
using Pointframe.Mcp.Configuration;
using Xunit;

namespace Pointframe.Tests.Mcp;

public sealed class DesktopTestingPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PointframePolicyTests", Guid.NewGuid().ToString("N"));
    private readonly DesktopTestingPolicyLoader _loader = new();

    public DesktopTestingPolicyTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void LoadAndValidate_LoadsStrictPolicyWithoutDesktopAccess()
    {
        var executablePath = CreateExecutable();
        var policyPath = WritePolicy(CreateValidPolicy(executablePath));

        var policy = _loader.LoadAndValidate(policyPath);

        Assert.Equal(DesktopTestingLimits.SchemaVersion, policy.SchemaVersion);
        Assert.Equal(Path.GetFullPath(_root), policy.ArtifactRoot);
        Assert.Single(policy.Profiles);
        Assert.Equal(executablePath, policy.Profiles[0].ExecutablePath);
        Assert.Contains(DesktopTestingAction.ListApps, policy.Profiles[0].AllowedActions);
    }

    [Theory]
    [InlineData("unknownField")]
    [InlineData("dataRoot")]
    [InlineData("environmentVariables")]
    [InlineData("launchMode")]
    public void LoadAndValidate_RejectsUnknownOrForbiddenProperties(string propertyName)
    {
        var executablePath = CreateExecutable();
        var policy = JsonSerializer.Deserialize<Dictionary<string, object>>(CreateValidPolicy(executablePath))!;
        policy[propertyName] = "not allowed";

        var exception = Assert.Throws<InvalidDataException>(() => _loader.LoadAndValidate(WritePolicy(JsonSerializer.Serialize(policy))));

        Assert.Contains(propertyName, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadAndValidate_RejectsRelativeAndMissingExecutablePaths()
    {
        var relativePolicy = CreateValidPolicy("target.exe");
        Assert.Throws<InvalidDataException>(() => _loader.LoadAndValidate(WritePolicy(relativePolicy)));

        var missingPolicy = CreateValidPolicy(Path.Combine(_root, "missing.exe"));
        Assert.Throws<InvalidDataException>(() => _loader.LoadAndValidate(WritePolicy(missingPolicy)));
    }

    [Fact]
    public void LoadAndValidate_RejectsDuplicateProfilesAndUnsupportedShellSurface()
    {
        var executablePath = CreateExecutable();
        var profile = JsonSerializer.Deserialize<JsonElement>(CreateValidPolicy(executablePath)).GetProperty("profiles")[0];
        var policy = $$"""
        {
          "schemaVersion": 1,
          "artifactRoot": {{JsonSerializer.Serialize(_root)}},
          "evidencePolicy": "Failures",
          "profiles": [{{profile.GetRawText()}}, {{profile.GetRawText()}}]
        }
        """;

        Assert.Throws<InvalidDataException>(() => _loader.LoadAndValidate(WritePolicy(policy)));

        var unsupportedSurfacePolicy = CreateValidPolicy(executablePath).Replace("NotificationArea", "explorerWindow", StringComparison.Ordinal);
        var exception = Assert.Throws<InvalidDataException>(() => _loader.LoadAndValidate(WritePolicy(unsupportedSurfacePolicy)));
        Assert.Contains("explorerWindow", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadAndValidate_RejectsDuplicateJsonProperties()
    {
        var executablePath = CreateExecutable();
        var policy = CreateValidPolicy(executablePath).Replace(
            "\"allowAttach\": false",
            "\"allowAttach\": false, \"allowAttach\": true",
            StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => _loader.LoadAndValidate(WritePolicy(policy)));
    }

    [Fact]
    public void LoadAndValidate_RejectsOversizedGlobalHotkey()
    {
        var executablePath = CreateExecutable();
        var policy = CreateValidPolicy(executablePath).Replace(
            "\"CTRL\", \"SHIFT\", \"P\"",
            "\"CTRL\", \"SHIFT\", \"P\", \"ALT\", \"X\"",
            StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => _loader.LoadAndValidate(WritePolicy(policy)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string CreateExecutable()
    {
        var path = Path.Combine(_root, "Target.exe");
        File.WriteAllBytes(path, [0]);
        return path;
    }

    private string WritePolicy(string content)
    {
        var path = Path.Combine(_root, $"policy-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        return path;
    }

    private string CreateValidPolicy(string executablePath)
    {
        return $$"""
        {
          "schemaVersion": 1,
          "artifactRoot": {{JsonSerializer.Serialize(_root)}},
          "evidencePolicy": "Failures",
          "profiles": [
            {
              "id": "pointframe",
              "executablePath": {{JsonSerializer.Serialize(executablePath)}},
              "arguments": [],
              "workingDirectory": {{JsonSerializer.Serialize(_root)}},
              "allowAttach": false,
              "allowedActions": ["ListApps", "StartTestSession", "ObserveApp"],
              "allowedGlobalHotkeys": {
                "capture": ["CTRL", "SHIFT", "P"]
              },
              "allowedShellSurfaces": ["NotificationArea"],
              "allowMonitorObservation": true
            }
          ]
        }
        """;
    }
}
