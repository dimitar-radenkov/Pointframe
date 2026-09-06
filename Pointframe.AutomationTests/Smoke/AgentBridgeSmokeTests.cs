using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Pointframe.AutomationTests.Fixtures;
using Pointframe.AutomationTests.Support;
using Xunit;

namespace Pointframe.AutomationTests.Smoke;

public sealed class AgentBridgeSmokeTests : IClassFixture<DesktopAutomationFixture>
{
    private readonly DesktopAutomationFixture _fixture;

    public AgentBridgeSmokeTests(DesktopAutomationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "DesktopAutomation")]
    public async Task PointframeCli_CaptureAndSave_WritesArtifactDescriptorJson()
    {
        _fixture.SeedSettings(autoSaveScreenshots: false);
        var environmentVariables = _fixture.CreateAgentBridgeEnvironmentVariables();
        using var app = AgentBridgeApp.Launch(environmentVariables);

        using var displays = await app.SendAsync("displays.list");
        var monitorName = displays.RootElement
            .GetProperty("displays")[0]
            .GetProperty("monitorName")
            .GetString();
        Assert.False(string.IsNullOrWhiteSpace(monitorName));

        var result = await RunCliAsync(environmentVariables, "capture", "--monitor", monitorName!);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
        using var output = JsonDocument.Parse(result.StandardOutput);
        var metadata = output.RootElement.GetProperty("metadata");
        var artifactPath = metadata.GetProperty("path").GetString();
        Assert.False(string.IsNullOrWhiteSpace(artifactPath));
        Assert.True(File.Exists(artifactPath));
        Assert.True(File.Exists($"{artifactPath}.metadata.json"));
    }

    [Fact]
    [Trait("Category", "DesktopAutomation")]
    public async Task AgentBridge_CaptureAndSave_WritesVerifiedPngAndMetadata()
    {
        _fixture.SeedSettings(autoSaveScreenshots: false);
        using var app = AgentBridgeApp.Launch(_fixture.CreateAgentBridgeEnvironmentVariables());

        using var displays = await app.SendAsync("displays.list");
        var monitorName = displays.RootElement
            .GetProperty("displays")[0]
            .GetProperty("monitorName")
            .GetString();
        Assert.False(string.IsNullOrWhiteSpace(monitorName));

        using var capture = await app.SendAsync("capture.monitor", monitorName);
        Assert.True(capture.RootElement.GetProperty("success").GetBoolean());
        Assert.True(capture.RootElement.GetProperty("state").GetProperty("canSave").GetBoolean());

        using var save = await app.SendAsync("overlay.save");
        Assert.True(save.RootElement.GetProperty("success").GetBoolean());
        var metadata = save.RootElement.GetProperty("artifact").GetProperty("metadata");
        var artifactPath = metadata.GetProperty("path").GetString();
        Assert.False(string.IsNullOrWhiteSpace(artifactPath));
        Assert.True(File.Exists(artifactPath));
        Assert.True(File.Exists($"{artifactPath}.metadata.json"));
        Assert.Equal(metadata.GetProperty("byteLength").GetInt64(), new FileInfo(artifactPath).Length);
        Assert.Equal(
            metadata.GetProperty("sha256").GetString(),
            Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(artifactPath))));
    }

    private static async Task<CliProcessResult> RunCliAsync(IReadOnlyDictionary<string, string> environmentVariables, params string[] arguments)
    {
        var cliAssemblyPath = Path.Combine(AppContext.BaseDirectory, "Pointframe.Cli.dll");
        Assert.True(File.Exists(cliAssemblyPath), $"CLI assembly was not found at '{cliAssemblyPath}'.");

        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(cliAssemblyPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var environmentVariable in environmentVariables)
        {
            startInfo.Environment[environmentVariable.Key] = environmentVariable.Value;
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Pointframe CLI process did not start.");
        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new CliProcessResult(
            process.ExitCode,
            await standardOutputTask,
            await standardErrorTask);
    }

    private sealed record CliProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
