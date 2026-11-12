using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Pointframe.Tests;

public sealed class WpfUiCollectionCoverageTests
{
    private static readonly Regex WpfTestClassPattern = new(@"\bclass\s+\w*Tests\b", RegexOptions.Compiled);
    private static readonly Regex WpfUsagePattern = new(
        @"\bStaTestHelper\s*\.\s*Run(?:Async)?\s*\(|\bApartmentState\s*\.\s*STA\b|\bnew\s+(?:(?:System\.Windows|System\.Windows\.Threading)\.)?(?:Window|Dispatcher)\b",
        RegexOptions.Compiled);

    [Fact]
    public void EveryWpfTestClassBelongsToWpfUiCollection()
    {
        // Explicit collection exemptions: none. Keep any future classes in another required collection listed here with their reason.
        var projectDirectory = FindTestProjectDirectory();
        var missingCollectionFiles = Directory
            .EnumerateFiles(projectDirectory.FullName, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path, projectDirectory))
            .Where(path =>
            {
                var source = File.ReadAllText(path);
                return WpfTestClassPattern.IsMatch(source) &&
                    WpfUsagePattern.IsMatch(source) &&
                    !Regex.IsMatch(source, @"\[Collection\s*\(\s*""WpfUi""\s*\)\]");
            })
            .Select(path => Path.GetRelativePath(projectDirectory.FullName, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missingCollectionFiles.Length == 0,
            $"WPF test classes must belong to [Collection(\"WpfUi\")]:{Environment.NewLine}{string.Join(Environment.NewLine, missingCollectionFiles)}");
    }

    private static DirectoryInfo FindTestProjectDirectory()
    {
        var directory = new FileInfo(Assembly.GetExecutingAssembly().Location).Directory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Pointframe.Tests.csproj")))
            {
                return directory;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Pointframe.Tests.csproj from the test assembly location.");
    }

    private static bool IsBuildOutput(string path, DirectoryInfo projectDirectory)
    {
        var relativePath = Path.GetRelativePath(projectDirectory.FullName, path);
        return relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment is "bin" or "obj");
    }
}
