namespace Pointframe.Cli;

internal interface ICliInstallEnvironment
{
    string? ProcessPath { get; }
    string LocalAppDataDirectory { get; }
    string? UserPath { get; }
    void SetUserPath(string value);
    IEnumerable<string> FilesIn(string directory);
    void CreateDirectory(string directory);
    void CopyFile(string source, string destination, bool overwrite);
}

internal sealed class PhysicalCliInstallEnvironment : ICliInstallEnvironment
{
    public string? ProcessPath => Environment.ProcessPath;
    public string LocalAppDataDirectory => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public string? UserPath => Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User);
    public void SetUserPath(string value) => Environment.SetEnvironmentVariable("Path", value, EnvironmentVariableTarget.User);
    public IEnumerable<string> FilesIn(string directory) => Directory.EnumerateFiles(directory);
    public void CreateDirectory(string directory) => Directory.CreateDirectory(directory);
    public void CopyFile(string source, string destination, bool overwrite) => File.Copy(source, destination, overwrite);
}

internal sealed class InstallApplication(ICliInstallEnvironment environment, IPointframeCommandResolver resolver)
{
    internal async Task<int> RunAsync(TextWriter output)
    {
        var target = Path.Combine(environment.LocalAppDataDirectory, "Programs", "Pointframe.Cli");
        var executable = Path.Combine(target, "pointframe.exe");
        var running = environment.ProcessPath;
        if (running is null)
        {
            throw new InvalidOperationException("The running executable path could not be determined.");
        }

        var alreadyInstalled = string.Equals(Path.GetFullPath(running), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase);
        if (!alreadyInstalled)
        {
            environment.CreateDirectory(target);
            var sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(running))!;
            // Only the executable, the native libraries beside it, and a bundled ffmpeg.exe: the ZIP may have been
            // extracted into a folder such as Downloads, whose other files must not be copied.
            environment.CopyFile(running, executable, overwrite: true);
            foreach (var source in environment.FilesIn(sourceDirectory))
            {
                if (string.Equals(Path.GetExtension(source), ".dll", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetFileName(source), "ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                {
                    environment.CopyFile(source, Path.Combine(target, Path.GetFileName(source)), overwrite: true);
                }
            }
        }

        var pathChanged = EnsureUserPath(target);

        var command = resolver.Resolve();
        var warning = command.Path is not null && !string.Equals(Path.GetFullPath(command.Path), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase)
            ? $"Another pointframe command earlier on PATH may shadow this install: {command.Path}"
            : null;
        var response = new
        {
            installedPath = executable,
            version = CliApplication.GetVersion(),
            alreadyInstalled,
            pathChanged,
            note = "Open a new terminal or restart the agent so it sees the updated PATH.",
            warning,
        };
        await output.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(response, VerificationApplication.VerdictJson));
        return 0;
    }

    private bool EnsureUserPath(string target)
    {
        var current = environment.UserPath ?? string.Empty;
        var entries = current.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        if (entries.Any(entry => Normalize(entry).Equals(Normalize(target), StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        environment.SetUserPath(current.Length == 0 ? target : current.TrimEnd(Path.PathSeparator) + Path.PathSeparator + target);
        return true;
    }

    private static string Normalize(string path) => Path.GetFullPath(path.Trim().Trim('"').TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
}
