using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Pointframe.Engine.Automation.Models;

namespace Pointframe.Engine.Automation.Services;

public interface IBlackBoxDesktopEnvironmentAdapter
{
    string UserName { get; }

    int SessionId { get; }

    bool IsInteractive { get; }

    bool IsUnlocked { get; }

    IReadOnlyList<BlackBoxDisplaySnapshot> GetDisplays();
}

public sealed class BlackBoxDesktopEnvironment
{
    private static readonly string[] ForbiddenEnvironmentVariables =
    [
        "SNIPPINGTOOL_AUTOMATION_SETTINGS_PATH",
        "SNIPPINGTOOL_AUTOMATION_OUTPUT_DIRECTORY",
        "SNIPPINGTOOL_AUTOMATION_OPEN_IMAGE_PATH",
        "POINTFRAME_AUTOMATION",
        "POINTFRAME_AUTOMATION_MODE",
    ];

    private readonly IBlackBoxDesktopEnvironmentAdapter _adapter;
    private readonly Func<string, string?> _getEnvironmentVariable;

    public BlackBoxDesktopEnvironment(
        IBlackBoxDesktopEnvironmentAdapter adapter,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _getEnvironmentVariable = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
    }

    public BlackBoxEnvironmentValidationResult ValidatePrerequisites(
        BlackBoxDesktopEnvironmentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = new List<string>();
        ValidateExecutablePath(options.PointframeExecutablePath, "Pointframe executable", errors);
        ValidateExecutablePath(options.McpExecutablePath, "MCP executable", errors);
        ValidateDirectory(options.ArtifactRoot, "Artifact root", errors);

        if (!options.DisposableEnvironmentAcknowledged)
        {
            errors.Add("The operator must acknowledge that the desktop is disposable and contains no production accounts or data.");
        }

        if (!_adapter.IsInteractive)
        {
            errors.Add("An interactive Windows desktop session is required.");
        }

        if (!_adapter.IsUnlocked)
        {
            errors.Add("The Windows desktop must be unlocked.");
        }

        foreach (var variable in ForbiddenEnvironmentVariables)
        {
            if (!string.IsNullOrWhiteSpace(_getEnvironmentVariable(variable)))
            {
                errors.Add($"Forbidden target automation environment variable '{variable}' is set.");
            }
        }

        BlackBoxDesktopEnvironmentSnapshot? snapshot = null;
        if (errors.Count == 0)
        {
            snapshot = CaptureEnvironment(options);
        }

        return new BlackBoxEnvironmentValidationResult(errors.Count == 0, errors, snapshot);
    }

    public BlackBoxDesktopEnvironmentSnapshot CaptureEnvironment(
        BlackBoxDesktopEnvironmentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var pointframePath = RequireExistingExecutable(options.PointframeExecutablePath, "Pointframe executable");
        var mcpPath = RequireExistingExecutable(options.McpExecutablePath, "MCP executable");
        ValidateDirectory(options.ArtifactRoot, "Artifact root", throwOnError: true);

        return new BlackBoxDesktopEnvironmentSnapshot(
            _adapter.UserName,
            _adapter.SessionId,
            _adapter.IsInteractive,
            _adapter.IsUnlocked,
            _adapter.GetDisplays(),
            pointframePath,
            ComputeSha256(pointframePath),
            mcpPath,
            ComputeSha256(mcpPath),
            DateTimeOffset.UtcNow);
    }

    private static void ValidateExecutablePath(string path, string label, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            errors.Add($"{label} path must be absolute.");
            return;
        }

        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"{label} path must point to an .exe file.");
        }

        if (!File.Exists(fullPath))
        {
            errors.Add($"{label} path must point to an existing file.");
        }
    }

    private static void ValidateDirectory(string path, string label, ICollection<string>? errors = null, bool throwOnError = false)
    {
        var error = string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
            ? $"{label} path must be absolute."
            : !Directory.Exists(Path.GetFullPath(path))
                ? $"{label} path must point to an existing directory."
                : null;

        if (error is null)
        {
            return;
        }

        if (throwOnError)
        {
            throw new InvalidDataException(error);
        }

        errors!.Add(error);
    }

    private static string RequireExistingExecutable(string path, string label)
    {
        var errors = new List<string>();
        ValidateExecutablePath(path, label, errors);
        if (errors.Count > 0)
        {
            throw new InvalidDataException(string.Join(" ", errors));
        }

        return Path.GetFullPath(path);
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

public sealed class WindowsDesktopEnvironmentAdapter : IBlackBoxDesktopEnvironmentAdapter
{
    private const uint DesktopReadObjects = 0x0001;
    private const int UserObjectName = 2;

    private readonly IDisplayCaptureEngine _displayCaptureEngine;

    public WindowsDesktopEnvironmentAdapter(IDisplayCaptureEngine displayCaptureEngine)
    {
        _displayCaptureEngine = displayCaptureEngine ?? throw new ArgumentNullException(nameof(displayCaptureEngine));
    }

    public string UserName => Environment.UserName;

    public int SessionId => Process.GetCurrentProcess().SessionId;

    public bool IsInteractive => Environment.UserInteractive;

    public bool IsUnlocked
    {
        get
        {
            if (!IsInteractive)
            {
                return false;
            }

            var desktop = OpenInputDesktop(0, false, DesktopReadObjects);
            if (desktop == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                _ = GetUserObjectInformation(desktop, UserObjectName, IntPtr.Zero, 0, out var requiredLength);
                if (requiredLength <= 0)
                {
                    return false;
                }

                var buffer = Marshal.AllocHGlobal(requiredLength);
                try
                {
                    if (!GetUserObjectInformation(desktop, UserObjectName, buffer, requiredLength, out _))
                    {
                        return false;
                    }

                    var name = Marshal.PtrToStringUni(buffer);
                    return string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CloseDesktop(desktop);
            }
        }
    }

    public IReadOnlyList<BlackBoxDisplaySnapshot> GetDisplays()
    {
        return _displayCaptureEngine
            .GetDisplays()
            .Select(display => new BlackBoxDisplaySnapshot(
                display.MonitorName,
                display.DpiScaleX,
                display.DpiScaleY,
                display.BoundsPixels,
                display.WorkAreaBoundsPixels))
            .ToArray();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(
        IntPtr handle,
        int index,
        IntPtr information,
        int length,
        out int requiredLength);
}
