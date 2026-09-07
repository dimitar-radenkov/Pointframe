using Pointframe.Engine.Automation.Models;
using Xunit;

namespace Pointframe.AutomationTests.Support;

public sealed class BlackBoxAppProfileFactoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PointframeProfileTests", Guid.NewGuid().ToString("N"));

    public BlackBoxAppProfileFactoryTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void CreatePointframeProfile_UsesEmptyArgumentsAndExplicitTrayAndHotkeyPermissions()
    {
        var executablePath = CreateExecutable("Pointframe.exe", [1]);

        var profile = BlackBoxAppProfileFactory.CreatePointframeProfile(
            executablePath,
            ["CTRL", "SHIFT", "P"]);

        Assert.Equal("pointframe", profile.Id);
        Assert.Empty(profile.Arguments);
        Assert.False(profile.AllowAttach);
        Assert.Null(profile.ApprovedAttachExecutablePath);
        Assert.False(profile.AllowMonitorObservation);
        Assert.Equal(["CTRL", "SHIFT", "P"], profile.AllowedGlobalHotkeys["capture"]);
        Assert.Contains(DesktopSurfaceKind.NotificationArea, profile.AllowedShellSurfaces);
        Assert.Contains(DesktopSurfaceKind.NotificationOverflow, profile.AllowedShellSurfaces);
        Assert.DoesNotContain(DesktopTestingAction.Drag, profile.AllowedActions);
        Assert.DoesNotContain(DesktopTestingAction.EnterText, profile.AllowedActions);
    }

    [Fact]
    public void CreateNotepadProfile_UsesOperatorExecutableWithoutSeedingOrLaunchOverrides()
    {
        var executablePath = CreateExecutable("Notepad.exe", [1]);

        var profile = BlackBoxAppProfileFactory.CreateNotepadProfile(executablePath);

        Assert.Equal("notepad", profile.Id);
        Assert.Equal(executablePath, profile.ExecutablePath);
        Assert.Empty(profile.Arguments);
        Assert.False(profile.AllowAttach);
        Assert.Null(profile.ApprovedAttachExecutablePath);
        Assert.Empty(profile.AllowedGlobalHotkeys);
        Assert.Empty(profile.AllowedShellSurfaces);
        Assert.DoesNotContain(DesktopTestingAction.PressKeys, profile.AllowedActions);
    }

    [Fact]
    public void CreateNotepadProfile_RequiresExplicitApprovedAttachExecutable()
    {
        var executablePath = CreateExecutable("Launcher.exe", [1]);
        var attachPath = CreateExecutable("Notepad.exe", [2]);

        var profile = BlackBoxAppProfileFactory.CreateNotepadProfile(
            executablePath,
            approvedAttachExecutablePath: attachPath);

        Assert.True(profile.AllowAttach);
        Assert.Equal(attachPath, profile.ApprovedAttachExecutablePath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string CreateExecutable(string name, byte[] content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, content);
        return path;
    }
}
