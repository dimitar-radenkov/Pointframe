using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace SnippingTool.AutomationTests.Support;

public sealed class AutomationApp : IDisposable
{
    private const string AutomationSettingsPathEnvironmentVariable = "SNIPPINGTOOL_AUTOMATION_SETTINGS_PATH";
    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(10);
    private readonly int _processId;
    private readonly UIA3Automation _automation;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private AutomationApp(Application application, UIA3Automation automation, Window mainWindow)
    {
        _processId = application.ProcessId;
        Application = application;
        _automation = automation;
        MainWindow = mainWindow;
    }

    public Application Application { get; }

    public Window MainWindow { get; private set; }

    public string MainWindowAutomationId => MainWindow.AutomationId;

    public static AutomationApp Launch(
        string automationArgument,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(automationArgument);

        var executablePath = Path.Combine(AppContext.BaseDirectory, "SnippingTool.exe");
        return LaunchExecutable(executablePath, automationArgument, environmentVariables);
    }

    public static AutomationApp LaunchExecutable(
        string executablePath,
        string automationArgument,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(automationArgument);

        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("SnippingTool.exe was not found at the requested automation launch path.", executablePath);
        }

        var startInfo = new ProcessStartInfo(executablePath, automationArgument)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
        };

        if (environmentVariables is not null)
        {
            foreach (var environmentVariable in environmentVariables)
            {
                startInfo.Environment[environmentVariable.Key] = environmentVariable.Value;
            }
        }

        var application = Application.Launch(startInfo);
        var automation = new UIA3Automation();
        var mainWindow = WaitForMainWindow(application, automation);
        return new AutomationApp(application, automation, mainWindow);
    }

    public static AutomationApp LaunchSettingsWindow(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        return Launch(
            "--automation-open-settings",
            new Dictionary<string, string>
            {
                [AutomationSettingsPathEnvironmentVariable] = settingsPath,
            });
    }

    public bool IsAutoSaveScreenshotsChecked()
    {
        var value = FindCheckBox(AutomationIds.SettingsWindowAutoSaveScreenshots).IsChecked;
        if (value is null)
        {
            throw new InvalidOperationException("Auto-save checkbox returned an indeterminate state.");
        }

        return value.Value;
    }

    public void ToggleAutoSaveScreenshots()
    {
        FindCheckBox(AutomationIds.SettingsWindowAutoSaveScreenshots).Toggle();
    }

    public void ClickButton(string automationId)
    {
        FindButton(automationId).Invoke();
    }

    public void ClickFirstButton(params string[] automationIds)
    {
        FindFirstRequiredElement(automationIds).AsButton().Invoke();
    }

    public void ClickSave()
    {
        ClickButton(AutomationIds.SettingsWindowSave);
    }

    public void ClickCancel()
    {
        ClickButton(AutomationIds.SettingsWindowCancel);
    }

    public void CloseMainWindow()
    {
        MainWindow.Close();
    }

    public void SwitchToTopLevelWindow(string automationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(automationId);
        MainWindow = WaitForTopLevelWindow(_processId, _automation, automationId);
    }

    public void WaitForExit()
    {
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < WindowTimeout)
        {
            if (!TryGetRunningProcess(out var process) || process is null)
            {
                return;
            }

            using (process)
            {
                if (process.HasExited)
                {
                    return;
                }
            }

            Thread.Sleep(100);
        }

        throw new TimeoutException("SnippingTool did not exit within the expected timeout.");
    }

    public void WaitForMainWindowToBeForeground()
    {
        var windowHandle = new IntPtr(MainWindow.Properties.NativeWindowHandle.ValueOrDefault);
        if (windowHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("The current automation window does not expose a native window handle.");
        }

        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < WindowTimeout)
        {
            if (GetForegroundWindow() == windowHandle)
            {
                return;
            }

            Thread.Sleep(100);
        }

        throw new TimeoutException("SnippingTool did not become the foreground window within the expected timeout.");
    }

    public void Dispose()
    {
        _automation.Dispose();

        try
        {
            if (TryGetRunningProcess(out var process) && process is not null)
            {
                using (process)
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit();
                    }
                }
            }
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            Application.Dispose();
        }
    }

    private static Window WaitForMainWindow(Application application, UIA3Automation automation)
    {
        return WaitForTopLevelWindow(application.ProcessId, automation);
    }

    private static Window WaitForTopLevelWindow(int processId, UIA3Automation automation, params string[] automationIds)
    {
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < WindowTimeout)
        {
            try
            {
                var windows = automation.GetDesktop()
                    .FindAllChildren(criteria => criteria.ByProcessId(processId));
                var window = automationIds.Length == 0
                    ? windows.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate.AutomationId))
                    : windows.FirstOrDefault(candidate =>
                        automationIds.Contains(candidate.AutomationId, StringComparer.Ordinal));
                if (window is not null)
                {
                    return window.AsWindow();
                }
            }
            catch (COMException)
            {
            }
            catch (Win32Exception)
            {
            }

            Thread.Sleep(100);
        }

        throw new TimeoutException("Timed out waiting for SnippingTool to open its automation window.");
    }

    private Button FindButton(string automationId)
    {
        var element = FindRequiredElement(automationId);
        return element.AsButton();
    }

    private CheckBox FindCheckBox(string automationId)
    {
        var element = FindRequiredElement(automationId);
        return element.AsCheckBox();
    }

    public AutomationElement FindRequiredElement(string automationId)
    {
        return FindFirstRequiredElement(automationId);
    }

    public AutomationElement FindFirstRequiredElement(params string[] automationIds)
    {
        ArgumentNullException.ThrowIfNull(automationIds);
        if (automationIds.Length == 0)
        {
            throw new ArgumentException("At least one automation ID must be provided.", nameof(automationIds));
        }

        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < WindowTimeout)
        {
            foreach (var automationId in automationIds)
            {
                var element = TryFindElement(automationId);
                if (element is not null)
                {
                    return element;
                }
            }

            Thread.Sleep(100);
        }

        throw new TimeoutException($"Timed out waiting for automation element '{string.Join("' or '", automationIds)}'.");
    }

    public AutomationElement? TryFindElement(string automationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(automationId);
        return MainWindow.FindFirstDescendant(criteria => criteria.ByAutomationId(automationId));
    }

    private bool TryGetRunningProcess(out Process? process)
    {
        try
        {
            process = Process.GetProcessById(_processId);
            return true;
        }
        catch (ArgumentException)
        {
            process = null;
            return false;
        }
    }
}
