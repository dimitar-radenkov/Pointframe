using System.IO;
using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class FileSystemServiceTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "SnippingToolTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void CreateDirectory_CreatesRequestedPath()
    {
        var sut = new FileSystemService();
        var path = Path.Combine(_rootPath, "nested", "folder");

        sut.CreateDirectory(path);

        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public void CombinePath_ReturnsCombinedPath()
    {
        var sut = new FileSystemService();

        var path = sut.CombinePath("root", "child.txt");

        Assert.Equal(Path.Combine("root", "child.txt"), path);
    }

    [Fact]
    public void OpenWrite_CreatesWritableStream()
    {
        var sut = new FileSystemService();
        sut.CreateDirectory(_rootPath);
        var path = Path.Combine(_rootPath, "sample.txt");

        using (var stream = sut.OpenWrite(path))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write("coverage");
        }

        Assert.Equal("coverage", File.ReadAllText(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }
}
