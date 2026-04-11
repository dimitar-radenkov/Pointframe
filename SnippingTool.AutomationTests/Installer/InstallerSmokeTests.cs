using System.Diagnostics;
using System.Security.Principal;
using SnippingTool.AutomationTests.Fixtures;
using SnippingTool.AutomationTests.Support;
using Xunit;

namespace SnippingTool.AutomationTests.Installer;

public sealed class InstallerSmokeTests : IClassFixture<DesktopAutomationFixture>
{
    private const string InstallerPathEnvironmentVariable = "SNIPPINGTOOL_AUTOMATION_INSTALLER_PATH";
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(2);
    private readonly DesktopAutomationFixture _fixture;

    public InstallerSmokeTests(DesktopAutomationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "InstallerSmoke")]
    public void SilentInstaller_InstallsLaunchesAndUninstalls()
    {
        EnsureAdministrator();
        var installerPath = Environment.GetEnvironmentVariable(InstallerPathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(installerPath))
        {
            throw new InvalidOperationException(
                $"Set {InstallerPathEnvironmentVariable} to the built SnippingTool installer path before running installer smoke tests.");
        }

        var installedApp = InstalledSnippingTool.Install(installerPath);
        try
        {
            Assert.True(File.Exists(installedApp.InstalledExecutablePath));
            Assert.True(File.Exists(installedApp.UninstallerPath));

            _fixture.SeedSettings(autoSaveScreenshots: false);

            using (var app = AutomationApp.LaunchExecutable(
                installedApp.InstalledExecutablePath,
                "--automation-open-about",
                _fixture.CreateEnvironmentVariables()))
            {
                Assert.Equal(AutomationIds.AboutWindowRoot, app.MainWindowAutomationId);
                app.ClickButton(AutomationIds.AboutWindowClose);
                app.WaitForExit();
            }

            installedApp.Uninstall();
            Assert.False(File.Exists(installedApp.InstalledExecutablePath));
        }
        finally
        {
            installedApp.Dispose();
        }
    }

    private static void EnsureAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
        {
            throw new InvalidOperationException(
                "Installer smoke tests must run from an elevated process because the Inno Setup installer requires administrator privileges.");
        }
    }

    private sealed class InstalledSnippingTool : IDisposable
    {
        private readonly string _workspaceDirectory;
        private bool _uninstalled;

        private InstalledSnippingTool(string workspaceDirectory)
        {
            _workspaceDirectory = workspaceDirectory;
        }

        public string InstallDirectory => Path.Combine(_workspaceDirectory, "Install");

        public string InstalledExecutablePath => Path.Combine(InstallDirectory, "SnippingTool.exe");

        public string UninstallerPath => Path.Combine(InstallDirectory, "unins000.exe");

        public static InstalledSnippingTool Install(string installerPath)
        {
            if (!File.Exists(installerPath))
            {
                throw new FileNotFoundException("The requested SnippingTool installer was not found.", installerPath);
            }

            var workspaceDirectory = Path.Combine(
                Path.GetTempPath(),
                "SnippingTool.InstallerSmoke",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workspaceDirectory);

            var installedApp = new InstalledSnippingTool(workspaceDirectory);
            var installLogPath = Path.Combine(workspaceDirectory, "install.log");
            RunProcess(
                installerPath,
                $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /DIR=\"{installedApp.InstallDirectory}\" /LOG=\"{installLogPath}\"",
                Path.GetDirectoryName(installerPath) ?? Environment.CurrentDirectory);

            if (!File.Exists(installedApp.InstalledExecutablePath))
            {
                throw new InvalidOperationException(
                    $"Silent installer finished, but '{installedApp.InstalledExecutablePath}' was not created.");
            }

            return installedApp;
        }

        public void Uninstall()
        {
            if (_uninstalled)
            {
                return;
            }

            if (!File.Exists(UninstallerPath))
            {
                throw new FileNotFoundException("The SnippingTool uninstaller was not found after installation.", UninstallerPath);
            }

            var uninstallLogPath = Path.Combine(_workspaceDirectory, "uninstall.log");
            RunProcess(
                UninstallerPath,
                $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=\"{uninstallLogPath}\"",
                InstallDirectory);

            _uninstalled = true;
        }

        public void Dispose()
        {
            if (!_uninstalled && File.Exists(UninstallerPath))
            {
                Uninstall();
            }

            if (_uninstalled && Directory.Exists(_workspaceDirectory))
            {
                Directory.Delete(_workspaceDirectory, recursive: true);
            }
        }

        private static void RunProcess(string fileName, string arguments, string workingDirectory)
        {
            var startInfo = new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory,
            };

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");
            if (!process.WaitForExit((int)ProcessTimeout.TotalMilliseconds))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                throw new TimeoutException($"'{Path.GetFileName(fileName)}' did not finish within {ProcessTimeout.TotalSeconds} seconds.");
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"'{Path.GetFileName(fileName)}' exited with code {process.ExitCode}.");
            }
        }
    }
}
