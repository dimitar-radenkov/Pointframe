namespace Pointframe.Engine.Automation.Models;

public sealed record DesktopLaunchRequest
{
    public DesktopLaunchRequest(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("An executable path is required.", nameof(executablePath));
        }

        if (!Path.IsPathFullyQualified(executablePath))
        {
            throw new ArgumentException("The executable path must be absolute.", nameof(executablePath));
        }

        ArgumentNullException.ThrowIfNull(arguments);

        if (string.IsNullOrWhiteSpace(workingDirectory) || !Path.IsPathFullyQualified(workingDirectory))
        {
            throw new ArgumentException("The working directory must be absolute.", nameof(workingDirectory));
        }

        ExecutablePath = executablePath;
        Arguments = arguments;
        WorkingDirectory = workingDirectory;
    }

    public string ExecutablePath { get; }

    public IReadOnlyList<string> Arguments { get; }

    public string WorkingDirectory { get; }
}

public sealed record DesktopTargetReference(
    string TargetRef,
    string ProfileId,
    int Generation,
    DesktopProcessIdentity Process,
    DesktopTargetState State,
    bool LaunchedByDriver);

public sealed record DesktopTestSessionSnapshot(
    string SessionRef,
    string ProfileId,
    DesktopSessionState State,
    DesktopTargetReference? Target);

public sealed record DesktopSessionOperationResult(
    bool Succeeded,
    string Code,
    string Message,
    DesktopTestSessionSnapshot? Session = null);
