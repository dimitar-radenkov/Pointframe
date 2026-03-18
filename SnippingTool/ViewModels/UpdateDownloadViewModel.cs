using System.Diagnostics;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SnippingTool.Services;

namespace SnippingTool.ViewModels;

public partial class UpdateDownloadViewModel : ObservableObject
{
    internal static readonly HttpClient SharedHttp = new()
    {
        DefaultRequestHeaders = { { "User-Agent", "SnippingTool" } },
    };

    private readonly HttpClient _http;
    private readonly IProcessService _process;
    private readonly ILogger<UpdateDownloadViewModel>? _logger;
    private CancellationTokenSource? _downloadCancellation;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private string _statusText = "Starting download…";

    [ObservableProperty]
    private bool _isDownloading = true;

    [ObservableProperty]
    private bool _isFailed;

    public event Action? RequestClose;

    public UpdateDownloadViewModel(
        HttpClient http,
        IProcessService process,
        ILogger<UpdateDownloadViewModel>? logger = null)
    {
        _http = http;
        _process = process;
        _logger = logger;
    }

    internal void AttachCancellation(CancellationTokenSource downloadCancellation)
    {
        _downloadCancellation = downloadCancellation;
    }

    public async Task DownloadAndInstallAsync(
        string downloadUrl,
        string destPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            var buffer = new byte[81920];
            var downloaded = 0L;

            await using (var src = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var dest = File.Create(destPath))
            {
                int read;
                while ((read = await src.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await dest.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    downloaded += read;

                    if (totalBytes > 0)
                    {
                        ProgressPercent = downloaded * 100.0 / totalBytes;
                        StatusText = $"Downloading… {ProgressPercent:F0}%";
                    }
                    else
                    {
                        StatusText = $"Downloading… {downloaded / 1024:N0} KB";
                    }
                }
            }

            ProgressPercent = 100;
            StatusText = "Download complete. Launching installer…";
            IsDownloading = false;

            _logger?.LogInformation("Update downloaded to {Path}", destPath);
            _logger?.LogInformation("Launching update installer from {Path}", destPath);

            _process.Start(new ProcessStartInfo(destPath) { UseShellExecute = true });
            RequestClose?.Invoke();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Download cancelled.";
            IsDownloading = false;
            RequestClose?.Invoke();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Update download failed");
            StatusText = "Download failed. Please try again.";
            IsDownloading = false;
            IsFailed = true;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (_downloadCancellation is not null && !_downloadCancellation.IsCancellationRequested)
        {
            _downloadCancellation.Cancel();
            return;
        }

        RequestClose?.Invoke();
    }
}
