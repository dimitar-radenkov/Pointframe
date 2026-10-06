using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Exceptions;
using FlaUI.Core.Input;
using FlaUI.UIA3;

namespace Pointframe.AutomationTests.Support;

public sealed class AutomationApp : IDisposable
{
    private const string AutomationSettingsPathEnvironmentVariable = "SNIPPINGTOOL_AUTOMATION_SETTINGS_PATH";
    private const string AutomationDataDirectoryEnvironmentVariable = "SNIPPINGTOOL_AUTOMATION_DATA_DIRECTORY";
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

        var executablePath = ResolveAutomationExecutablePath();
        return LaunchExecutable(executablePath, automationArgument, environmentVariables);
    }

    public static AutomationApp LaunchExecutable(
        string executablePath,
        string automationArgument,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("Pointframe.exe was not found at the requested automation launch path.", executablePath);
        }

        var startInfo = new ProcessStartInfo(executablePath, automationArgument ?? string.Empty)
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
        try
        {
            var mainWindow = WaitForMainWindow(application, automation);
            return new AutomationApp(application, automation, mainWindow);
        }
        catch
        {
            automation.Dispose();
            KillLaunchedProcess(application);
            throw;
        }
    }

    private static void KillLaunchedProcess(Application application)
    {
        try
        {
            using var process = Process.GetProcessById(application.ProcessId);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
        finally
        {
            application.Dispose();
        }
    }

    public static AutomationApp LaunchNormally(IReadOnlyDictionary<string, string>? environmentVariables = null) =>
        LaunchExecutable(ResolveAutomationExecutablePath(), string.Empty, environmentVariables);

    public static Process StartExecutableWithoutWaiting(
        string executablePath,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        var startInfo = new ProcessStartInfo(executablePath, string.Empty)
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

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Pointframe did not start.");
    }

    public bool HasWindowAutomationId(string automationId) => GetProcessWindows(_processId, _automation)
        .Any(window => string.Equals(window.Properties.AutomationId.ValueOrDefault, automationId, StringComparison.Ordinal));

    public void WaitForWindowTitle(string title)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < WindowTimeout)
        {
            var candidate = GetProcessWindows(_processId, _automation)
                .FirstOrDefault(window => string.Equals(window.Name, title, StringComparison.Ordinal));
            if (candidate is not null)
            {
                MainWindow = candidate.AsWindow();
                return;
            }

            Thread.Sleep(100);
        }

        var seen = string.Join(", ", GetProcessWindows(_processId, _automation).Select(window => $"'{window.Properties.Name.ValueOrDefault}'"));
        throw new TimeoutException($"Timed out waiting for window '{title}'. Process windows: [{seen}].");
    }

    public void WaitForWindowTitleToClose(string title)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < WindowTimeout)
        {
            var isOpen = GetProcessWindows(_processId, _automation)
                .Any(window => string.Equals(window.Name, title, StringComparison.Ordinal));
            if (!isOpen)
            {
                return;
            }

            Thread.Sleep(100);
        }

        throw new TimeoutException($"Window '{title}' did not close within the expected timeout.");
    }

    public static AutomationApp LaunchSettingsWindow(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        var dataDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settingsPath))!, "Data");
        Directory.CreateDirectory(dataDirectory);
        return Launch(
            "--automation-open-settings",
            new Dictionary<string, string>
            {
                [AutomationSettingsPathEnvironmentVariable] = settingsPath,
                [AutomationDataDirectoryEnvironmentVariable] = dataDirectory,
            });
    }

    internal static string ResolveAutomationExecutablePath()
    {
        var directPath = Path.Combine(AppContext.BaseDirectory, "Pointframe.exe");
        if (File.Exists(directPath))
        {
            return directPath;
        }

        var repositoryRoot = FindRepositoryRoot();
        if (repositoryRoot is not null)
        {
            var binRoot = Path.Combine(repositoryRoot.FullName, "Pointframe", "bin");
            if (Directory.Exists(binRoot))
            {
                var builtExecutable = Directory
                    .EnumerateFiles(binRoot, "Pointframe.exe", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();

                if (!string.IsNullOrWhiteSpace(builtExecutable))
                {
                    return builtExecutable;
                }
            }
        }

        return directPath;
    }

    private static DirectoryInfo? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var appProjectDirectory = Path.Combine(directory.FullName, "Pointframe");
            var automationProjectDirectory = Path.Combine(directory.FullName, "Pointframe.AutomationTests");

            if (Directory.Exists(appProjectDirectory) && Directory.Exists(automationProjectDirectory))
            {
                return directory;
            }

            directory = directory.Parent;
        }

        return null;
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

    public void HoverElement(string automationId)
    {
        var element = FindRequiredElement(automationId);
        Mouse.MoveTo(element.GetClickablePoint());
    }

    public void SelectRadioButton(string automationId)
    {
        FindRequiredElement(automationId).AsRadioButton().Click();
    }

    public void SelectListItem(string automationId)
    {
        FindRequiredElement(automationId).AsListBoxItem().Select();
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
                        process.WaitForExit((int)WindowTimeout.TotalMilliseconds);
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

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    internal static AutomationElement[] GetProcessWindows(int processId, UIA3Automation automation)
    {
        var windows = new List<AutomationElement>();
        foreach (var handle in GetTopLevelWindowHandles(processId))
        {
            try
            {
                windows.Add(automation.FromHandle(handle));
            }
            catch (ElementNotAvailableException)
            {
            }
            catch (COMException)
            {
            }
        }

        return [.. windows];
    }

    private static List<IntPtr> GetTopLevelWindowHandles(int processId)
    {
        var handles = new List<IntPtr>();
        EnumWindows(
            (handle, _) =>
            {
                GetWindowThreadProcessId(handle, out var owner);
                if (owner == (uint)processId)
                {
                    handles.Add(handle);
                }

                return true;
            },
            IntPtr.Zero);
        return handles;
    }

    private static Window WaitForMainWindow(Application application, UIA3Automation automation)
    {
        return WaitForTopLevelWindow(application.ProcessId, automation);
    }

    private static Window WaitForTopLevelWindow(int processId, UIA3Automation automation, params string[] automationIds)
    {
        var stopwatch = Stopwatch.StartNew();
        var attempts = 0;
        var failures = 0;
        var lastWindowCount = -1;
        string? lastFailure = null;
        string lastWindows = string.Empty;

        while (stopwatch.Elapsed < WindowTimeout)
        {
            attempts++;
            try
            {
                var handles = GetTopLevelWindowHandles(processId);
                var windows = handles
                    .Select(handle => automation.FromHandle(handle))
                    .Where(element => element is not null)
                    .ToArray();
                lastWindowCount = windows.Length;
                lastWindows = $"{handles.Count} native handle(s); " + string.Join(", ", windows.Select(candidate => $"'{candidate.Properties.AutomationId.ValueOrDefault}'"));
                var window = automationIds.Length == 0
                    ? windows.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate.Properties.AutomationId.ValueOrDefault))
                    : windows.FirstOrDefault(candidate =>
                        automationIds.Contains(candidate.Properties.AutomationId.ValueOrDefault, StringComparer.Ordinal));
                if (window is not null)
                {
                    return window.AsWindow();
                }
            }
            catch (COMException ex)
            {
                failures++;
                lastFailure = $"{ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}";
            }
            catch (ElementNotAvailableException ex)
            {
                failures++;
                lastFailure = $"{ex.GetType().Name} {ex.InnerException?.Message ?? ex.Message}";
            }
            catch (TimeoutException ex)
            {
                failures++;
                lastFailure = $"{ex.GetType().Name} {ex.InnerException?.Message ?? ex.Message}";
            }
            catch (Win32Exception ex)
            {
                failures++;
                lastFailure = $"{ex.GetType().Name} {ex.Message}";
            }

            Thread.Sleep(100);
        }

        var exited = false;
        try
        {
            using var process = Process.GetProcessById(processId);
            exited = process.HasExited;
        }
        catch (ArgumentException)
        {
            exited = true;
        }

        throw new TimeoutException(
            $"Timed out waiting for SnippingTool to open its automation window. Process {processId} exited={exited}; "
            + $"{attempts} attempts, {failures} UIA failures (last: {lastFailure ?? "none"}); "
            + $"last enumeration saw {lastWindowCount} window(s) [{lastWindows}]; wanted [{string.Join(", ", automationIds)}].");
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
