using System.IO;

namespace Pointframe.Services;

public interface IFileSystemService
{
    void CreateDirectory(string path);
    string CombinePath(string path1, string path2);
    Stream OpenWrite(string path);
}
