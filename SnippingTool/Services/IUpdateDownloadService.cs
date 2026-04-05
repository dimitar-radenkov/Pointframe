namespace SnippingTool.Services;

public interface IUpdateDownloadService
{
    Task<bool> Show(string downloadUrl, string destPath);
}
