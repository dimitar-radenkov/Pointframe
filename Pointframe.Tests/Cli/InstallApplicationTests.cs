using System.IO;
using System.Text.Json;
using Moq;
using Pointframe.Cli;
using Xunit;

namespace Pointframe.Tests.Cli;

public sealed class InstallApplicationTests
{
    [Fact]
    public async Task Install_CopiesRuntimeFilesAndAddsUserPath()
    {
        var environment = new FakeInstallEnvironment("C:\\zip\\Pointframe.Cli.exe", "C:\\Users\\test\\AppData\\Local", null);
        environment.Files["C:\\zip"] = ["C:\\zip\\Pointframe.Cli.exe", "C:\\zip\\e_sqlite3.dll", "C:\\zip\\Pointframe.Cli.pdb", "C:\\zip\\ffmpeg.exe", "C:\\zip\\holiday.jpg"];
        var resolver = new Mock<IPointframeCommandResolver>();
        resolver.Setup(item => item.Resolve(null)).Returns(new PointframeCommandInfo("C:\\Users\\test\\AppData\\Local\\Programs\\Pointframe.Cli\\pointframe.exe", "Pointframe CLI 1", true));
        var output = new StringWriter();

        Assert.Equal(0, await new InstallApplication(environment, resolver.Object).RunAsync(output));

        Assert.Equal("C:\\Users\\test\\AppData\\Local\\Programs\\Pointframe.Cli", environment.CreatedDirectory);
        Assert.Equal("C:\\Users\\test\\AppData\\Local\\Programs\\Pointframe.Cli", environment.SavedPath);
        Assert.Equal(["pointframe.exe", "e_sqlite3.dll", "ffmpeg.exe"], environment.CopiedFiles);
        using var result = JsonDocument.Parse(output.ToString());
        Assert.True(result.RootElement.GetProperty("pathChanged").GetBoolean());
    }

    [Fact]
    public async Task Install_ExistingInstallAndTrailingSlashPathAreIdempotent()
    {
        const string target = "C:\\Users\\test\\AppData\\Local\\Programs\\Pointframe.Cli";
        var environment = new FakeInstallEnvironment(target + "\\pointframe.exe", "C:\\Users\\test\\AppData\\Local", target + "\\");
        var resolver = new Mock<IPointframeCommandResolver>();
        resolver.Setup(item => item.Resolve(null)).Returns(new PointframeCommandInfo(target + "\\pointframe.exe", "Pointframe CLI 1", true));

        var output = new StringWriter();
        Assert.Equal(0, await new InstallApplication(environment, resolver.Object).RunAsync(output));

        Assert.Null(environment.CreatedDirectory);
        Assert.Null(environment.SavedPath);
        using var result = JsonDocument.Parse(output.ToString());
        Assert.True(result.RootElement.GetProperty("alreadyInstalled").GetBoolean());
        Assert.False(result.RootElement.GetProperty("pathChanged").GetBoolean());
    }

    [Fact]
    public async Task Install_UpdateOverwritesTheExistingExecutable()
    {
        var environment = new FakeInstallEnvironment("C:\\zip\\Pointframe.Cli.exe", "C:\\Users\\test\\AppData\\Local", null);
        environment.Files["C:\\zip"] = ["C:\\zip\\Pointframe.Cli.exe", "C:\\zip\\e_sqlite3.dll"];
        var resolver = MatchingResolver("C:\\Users\\test\\AppData\\Local\\Programs\\Pointframe.Cli\\pointframe.exe");

        await new InstallApplication(environment, resolver.Object).RunAsync(new StringWriter());

        Assert.Contains("pointframe.exe", environment.CopiedFiles);
        Assert.Contains("e_sqlite3.dll", environment.CopiedFiles);
    }

    [Fact]
    public async Task Install_DoesNotDuplicateAnExistingUserPathEntry()
    {
        const string target = "C:\\Users\\test\\AppData\\Local\\Programs\\Pointframe.Cli";
        var environment = new FakeInstallEnvironment("C:\\zip\\Pointframe.Cli.exe", "C:\\Users\\test\\AppData\\Local", target + "\\");
        environment.Files["C:\\zip"] = ["C:\\zip\\Pointframe.Cli.exe"];

        await new InstallApplication(environment, MatchingResolver(target + "\\pointframe.exe").Object).RunAsync(new StringWriter());

        Assert.Null(environment.SavedPath);
    }

    private static Mock<IPointframeCommandResolver> MatchingResolver(string path)
    {
        var resolver = new Mock<IPointframeCommandResolver>();
        resolver.Setup(item => item.Resolve(null)).Returns(new PointframeCommandInfo(path, "Pointframe CLI 1", true));
        return resolver;
    }

    private sealed class FakeInstallEnvironment(string processPath, string localAppData, string? userPath) : ICliInstallEnvironment
    {
        public string? ProcessPath { get; } = processPath;
        public string LocalAppDataDirectory { get; } = localAppData;
        public string? UserPath { get; private set; } = userPath;
        public Dictionary<string, string[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> CopiedFiles { get; } = [];
        public string? CreatedDirectory { get; private set; }
        public string? SavedPath { get; private set; }
        public IEnumerable<string> FilesIn(string directory) => Files.GetValueOrDefault(directory, []);
        public void CreateDirectory(string directory) => CreatedDirectory = directory;
        public void SetUserPath(string value)
        {
            SavedPath = value;
            UserPath = value;
        }
        public void CopyFile(string source, string destination, bool overwrite) => CopiedFiles.Add(Path.GetFileName(destination));
    }
}
