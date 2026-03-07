using System.IO;

namespace SnippingTool.Services;

public sealed class FileSystemService : IFileSystemService
{
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public string CombinePath(string path1, string path2) => Path.Combine(path1, path2);

    public Stream OpenWrite(string path) => File.OpenWrite(path);
}
