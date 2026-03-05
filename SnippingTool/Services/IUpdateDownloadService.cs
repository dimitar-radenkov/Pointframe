namespace SnippingTool.Services;

public interface IUpdateDownloadService
{
    Task<bool> ShowAsync(string downloadUrl, string destPath);
}
