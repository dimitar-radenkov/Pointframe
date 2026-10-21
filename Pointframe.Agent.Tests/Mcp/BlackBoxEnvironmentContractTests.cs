using System.IO;
using System.Security.Cryptography;
using Pointframe.Engine;
using Pointframe.Engine.Automation.Models;
using Pointframe.Engine.Automation.Services;
using Xunit;

namespace Pointframe.Tests.Mcp;

public sealed class BlackBoxEnvironmentContractTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PointframeEnvironmentTests", Guid.NewGuid().ToString("N"));

    public BlackBoxEnvironmentContractTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void ValidatePrerequisites_RequiresDisposableInteractiveUnlockedEnvironment()
    {
        var options = CreateOptions();
        var adapter = new FakeEnvironmentAdapter(isInteractive: false, isUnlocked: false);
        var environment = new BlackBoxDesktopEnvironment(adapter);

        var result = environment.ValidatePrerequisites(options with { DisposableEnvironmentAcknowledged = false });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("disposable", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("interactive", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("unlocked", StringComparison.OrdinalIgnoreCase));
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public void ValidatePrerequisites_RejectsKnownTargetAutomationEnvironmentVariables()
    {
        var options = CreateOptions();
        var adapter = new FakeEnvironmentAdapter(isInteractive: true, isUnlocked: true);
        var environment = new BlackBoxDesktopEnvironment(
            adapter,
            variable => variable == "SNIPPINGTOOL_AUTOMATION_SETTINGS_PATH" ? "C:\\private\\settings.json" : null);

        var result = environment.ValidatePrerequisites(options);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("SNIPPINGTOOL_AUTOMATION_SETTINGS_PATH", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatePrerequisites_RejectsRelativeOrMissingRunnerPaths()
    {
        var adapter = new FakeEnvironmentAdapter(isInteractive: true, isUnlocked: true);
        var environment = new BlackBoxDesktopEnvironment(adapter);

        var result = environment.ValidatePrerequisites(CreateOptions() with
        {
            PointframeExecutablePath = "Pointframe.exe",
            McpExecutablePath = Path.Combine(_root, "missing.exe"),
        });

        Assert.False(result.IsValid);
        Assert.True(result.Errors.Count >= 2);
    }

    [Fact]
    public void CaptureEnvironment_RecordsUserSessionDisplaysAndExecutableHashes()
    {
        var pointframePath = CreateExecutable("Pointframe.exe", [1, 2, 3]);
        var mcpPath = CreateExecutable("Pointframe.Mcp.exe", [4, 5, 6]);
        var adapter = new FakeEnvironmentAdapter(isInteractive: true, isUnlocked: true);
        var environment = new BlackBoxDesktopEnvironment(adapter);
        var options = new BlackBoxDesktopEnvironmentOptions(
            pointframePath,
            mcpPath,
            _root,
            true);

        var snapshot = environment.CaptureEnvironment(options);

        Assert.Equal("test-user", snapshot.UserName);
        Assert.Equal(7, snapshot.SessionId);
        Assert.True(snapshot.IsInteractive);
        Assert.True(snapshot.IsUnlocked);
        Assert.Single(snapshot.Displays);
        Assert.Equal(Convert.ToHexString(SHA256.HashData([1, 2, 3])), snapshot.PointframeExecutableSha256);
        Assert.Equal(Convert.ToHexString(SHA256.HashData([4, 5, 6])), snapshot.McpExecutableSha256);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private BlackBoxDesktopEnvironmentOptions CreateOptions()
    {
        return new BlackBoxDesktopEnvironmentOptions(
            CreateExecutable("Pointframe.exe", [1]),
            CreateExecutable("Pointframe.Mcp.exe", [2]),
            _root,
            true);
    }

    private string CreateExecutable(string name, byte[] content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private sealed class FakeEnvironmentAdapter(bool isInteractive, bool isUnlocked) : IBlackBoxDesktopEnvironmentAdapter
    {
        public string UserName => "test-user";

        public int SessionId => 7;

        public bool IsInteractive => isInteractive;

        public bool IsUnlocked => isUnlocked;

        public IReadOnlyList<BlackBoxDisplaySnapshot> GetDisplays()
        {
            return
            [
                new BlackBoxDisplaySnapshot(
                    "\\\\.\\DISPLAY1",
                    1,
                    1,
                    new PixelBounds(0, 0, 1920, 1080),
                    new PixelBounds(0, 0, 1920, 1040)),
            ];
        }
    }
}
