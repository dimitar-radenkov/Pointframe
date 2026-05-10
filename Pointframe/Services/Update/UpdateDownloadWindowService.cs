using Pointframe.ViewModels;

namespace Pointframe.Services;

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

    public async Task<bool> Show(string downloadUrl, string destPath)
    {
        var vm = _vmFactory();
        using var downloadCancellation = new CancellationTokenSource();
        var downloadToken = downloadCancellation.Token;
        vm.AttachCancellation(downloadCancellation);

        var window = _windowFactory(vm);
        var downloadCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var downloadStarted = false;
        var windowClosed = false;

        async void StartDownload(object? sender, EventArgs e)
        {
            window.ContentRendered -= StartDownload;

            if (windowClosed)
            {
                downloadCompleted.TrySetResult();
                return;
            }

            downloadStarted = true;

            try
            {
                await vm.DownloadAndInstallAsync(downloadUrl, destPath, downloadToken);
            }
            finally
            {
                downloadCompleted.TrySetResult();
            }
        }

        void HandleWindowClosed(object? sender, EventArgs e)
        {
            windowClosed = true;
            window.ContentRendered -= StartDownload;

            if (!downloadStarted || (vm.IsDownloading && !downloadCancellation.IsCancellationRequested))
            {
                downloadCancellation.Cancel();
            }

            if (!downloadStarted)
            {
                downloadCompleted.TrySetResult();
            }

            window.Closed -= HandleWindowClosed;
        }

        window.ContentRendered += StartDownload;
        window.Closed += HandleWindowClosed;
        window.ShowDialog();

        await downloadCompleted.Task;
        return !vm.IsFailed && !downloadCancellation.IsCancellationRequested;
    }
}
