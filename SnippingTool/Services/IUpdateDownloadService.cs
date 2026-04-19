namespace Pointframe.Services;

public interface IUpdateDownloadService
{
    Task<bool> Show(string downloadUrl, string destPath);
}
