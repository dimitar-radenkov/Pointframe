using Pointframe.Engine.Automation.Models;

namespace Pointframe.AutomationTests.Support;

public static class BlackBoxAppProfileFactory
{
    private static readonly IReadOnlySet<DesktopTestingAction> PointframeActions =
        new HashSet<DesktopTestingAction>
        {
            DesktopTestingAction.ListApps,
            DesktopTestingAction.StartTestSession,
            DesktopTestingAction.RestartApp,
            DesktopTestingAction.ObserveApp,
            DesktopTestingAction.FocusWindow,
            DesktopTestingAction.Click,
            DesktopTestingAction.PressKeys,
            DesktopTestingAction.CheckUi,
            DesktopTestingAction.GetActionResult,
            DesktopTestingAction.GetTestReport,
            DesktopTestingAction.EndTestSession,
        };

    private static readonly IReadOnlySet<DesktopTestingAction> NotepadActions =
        new HashSet<DesktopTestingAction>
        {
            DesktopTestingAction.ListApps,
            DesktopTestingAction.StartTestSession,
            DesktopTestingAction.RestartApp,
            DesktopTestingAction.ObserveApp,
            DesktopTestingAction.FocusWindow,
            DesktopTestingAction.Click,
            DesktopTestingAction.CheckUi,
            DesktopTestingAction.GetActionResult,
            DesktopTestingAction.GetTestReport,
            DesktopTestingAction.EndTestSession,
        };

    public static BlackBoxAppProfile CreatePointframeProfile(
        string executablePath,
        IReadOnlyList<string> captureHotkeyKeys,
        string? workingDirectory = null)
    {
        var normalizedExecutablePath = RequireExecutable(executablePath, nameof(executablePath));
        var normalizedWorkingDirectory = RequireDirectory(
            workingDirectory ?? Path.GetDirectoryName(normalizedExecutablePath)!,
            nameof(workingDirectory));
        var hotkey = RequireHotkey(captureHotkeyKeys, nameof(captureHotkeyKeys));

        return new BlackBoxAppProfile(
            "pointframe",
            normalizedExecutablePath,
            Array.Empty<string>(),
            normalizedWorkingDirectory,
            false,
            null,
            PointframeActions,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["capture"] = hotkey,
            },
            new HashSet<DesktopSurfaceKind>
            {
                DesktopSurfaceKind.NotificationArea,
                DesktopSurfaceKind.NotificationOverflow,
            },
            false);
    }

    public static BlackBoxAppProfile CreateNotepadProfile(
        string executablePath,
        string? workingDirectory = null,
        string? approvedAttachExecutablePath = null)
    {
        var normalizedExecutablePath = RequireExecutable(executablePath, nameof(executablePath));
        var normalizedWorkingDirectory = RequireDirectory(
            workingDirectory ?? Path.GetDirectoryName(normalizedExecutablePath)!,
            nameof(workingDirectory));
        var normalizedAttachPath = approvedAttachExecutablePath is null
            ? null
            : RequireExecutable(approvedAttachExecutablePath, nameof(approvedAttachExecutablePath));

        return new BlackBoxAppProfile(
            "notepad",
            normalizedExecutablePath,
            Array.Empty<string>(),
            normalizedWorkingDirectory,
            normalizedAttachPath is not null,
            normalizedAttachPath,
            NotepadActions,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
            new HashSet<DesktopSurfaceKind>(),
            false);
    }

    private static string RequireExecutable(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The executable path must be absolute.", parameterName);
        }

        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The executable path must point to an .exe file.", parameterName);
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The executable was not found.", fullPath);
        }

        return fullPath;
    }

    private static string RequireDirectory(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The working directory must be absolute.", parameterName);
        }

        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"The working directory was not found: {fullPath}");
        }

        return fullPath;
    }

    private static IReadOnlyList<string> RequireHotkey(IReadOnlyList<string> keys, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0 || keys.Count > DesktopTestingLimits.MaxSimultaneousKeys)
        {
            throw new ArgumentException(
                $"The hotkey must contain 1 to {DesktopTestingLimits.MaxSimultaneousKeys} keys.",
                parameterName);
        }

        if (keys.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("The hotkey cannot contain empty keys.", parameterName);
        }

        return keys.ToArray();
    }
}
