using System.Diagnostics;

namespace Pointframe.Cli;

internal sealed record WorkingTreeState(string? Head, string? TreeHash);

internal interface IWorkingTreeReader
{
    WorkingTreeState Read(string directory);

    // Everything that differs from baseCommit, untracked files included, for the reviewer.
    string? Diff(string directory, string baseCommit);
}

// The same tree hash as scripts/verify.ps1: stage everything (respecting .gitignore) into a copy of the
// index and write the tree, so the hash covers uncommitted and untracked files without touching the
// real index. Outside a git repository, or when staging fails (a locked file), the hash is null: an
// unknown tree never matches a verdict, so a stale pass cannot be reused.
internal sealed class GitWorkingTreeReader : IWorkingTreeReader
{
    // verify's own output lives in the project; without this, every run would change the hash it is
    // compared against in a project that does not ignore artifacts/, and evidence images would be hashed.
    private const string OwnOutputPath = "artifacts/pointframe-verify";

    public WorkingTreeState Read(string directory)
    {
        var head = Git(directory, null, "rev-parse", "HEAD").Output;
        var indexPath = Git(directory, null, "rev-parse", "--path-format=absolute", "--git-path", "index").Output;
        if (indexPath is null)
        {
            return new WorkingTreeState(head, null);
        }

        return new WorkingTreeState(head, WithStagedCopy(directory, indexPath, environment => Git(directory, environment, "write-tree").Output));
    }

    public string? Diff(string directory, string baseCommit)
    {
        var indexPath = Git(directory, null, "rev-parse", "--path-format=absolute", "--git-path", "index").Output;
        return indexPath is null
            ? null
            : WithStagedCopy(directory, indexPath, environment => Git(directory, environment, "diff", "--cached", "--no-color", baseCommit).Output);
    }

    // Stages every file (respecting .gitignore) into a throwaway copy of the index, so hashes and diffs cover
    // uncommitted and untracked work without touching the user's real index.
    private static string? WithStagedCopy(string directory, string indexPath, Func<IReadOnlyDictionary<string, string>, string?> action)
    {
        var tempIndex = Path.Combine(Path.GetTempPath(), $"pointframe-verify-{Guid.NewGuid():N}.index");
        try
        {
            if (File.Exists(indexPath))
            {
                File.Copy(indexPath, tempIndex);
            }

            // Stage everything, then drop verify's own output from the copy. An exclude pathspec on `git add`
            // fails outright when that folder is also in .gitignore (as artifacts/ usually is), which made the
            // hash unknown in exactly the projects that ignore it.
            var environment = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = tempIndex };
            return Git(directory, environment, "add", "-A").ExitCode == 0
                && Git(directory, environment, "rm", "-r", "-q", "--cached", "--ignore-unmatch", "--", OwnOutputPath).ExitCode == 0
                ? action(environment)
                : null;
        }
        finally
        {
            File.Delete(tempIndex);
        }
    }

    private static (int ExitCode, string? Output) Git(string directory, IReadOnlyDictionary<string, string>? environment, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            startInfo.Environment[name] = value;
        }

        try
        {
            using var process = Process.Start(startInfo)!;
            var errorTask = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            _ = errorTask.Result;
            return (process.ExitCode, process.ExitCode == 0 && output.Length > 0 ? output : null);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (-1, null);
        }
    }
}
