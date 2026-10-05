namespace Pointframe.Cli;

// A path that looks inside the project can still leave it through a symlink or junction, so containment is
// decided on the real location: every existing component is followed to its final target.
internal static class PathContainment
{
    internal static string Resolve(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var current = root;
        var remaining = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < remaining.Length; index++)
        {
            var next = Path.Combine(current, remaining[index]);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (!info.Exists)
            {
                return Path.GetFullPath(Path.Combine([next, .. remaining[(index + 1)..]]));
            }

            try
            {
                current = info.LinkTarget is null ? next : info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? next;
            }
            catch (IOException)
            {
                current = next;
            }
        }

        return Path.TrimEndingDirectorySeparator(current);
    }

    internal static bool IsInside(string path, string root)
    {
        var resolvedRoot = Path.TrimEndingDirectorySeparator(Resolve(root)) + Path.DirectorySeparatorChar;
        var resolvedPath = Resolve(path);
        return resolvedPath.StartsWith(resolvedRoot, StringComparison.OrdinalIgnoreCase)
            || string.Equals(resolvedPath + Path.DirectorySeparatorChar, resolvedRoot, StringComparison.OrdinalIgnoreCase);
    }
}
