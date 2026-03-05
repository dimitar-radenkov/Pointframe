using SnippingTool.ViewModels;

namespace SnippingTool.Services;

public sealed class UpdateDownloadWindowService : IUpdateDownloadService
{
    private readonly Func<UpdateDownloadViewModel> _vmFactory;
    private readonly Func<UpdateDownloadViewModel, UpdateDownloadWindow> _windowFactory;

    public UpdateDownloadWindowService(
        Func<UpdateDownloadViewModel> vmFactory,
        Func<UpdateDownloadViewModel, UpdateDownloadWindow> windowFactory)
    {
        _vmFactory = vmFactory;
        _windowFactory = windowFactory;
    }

    public async Task<bool> ShowAsync(string downloadUrl, string destPath)
    {
        var vm = _vmFactory();
        var window = _windowFactory(vm);
        window.Show();
        await vm.DownloadAndInstallAsync(downloadUrl, destPath);
        return !vm.IsFailed;
    }
}
