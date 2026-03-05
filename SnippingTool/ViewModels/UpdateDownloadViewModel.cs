using System.Diagnostics;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace SnippingTool.ViewModels;

public partial class UpdateDownloadViewModel : ObservableObject
{
    private static readonly HttpClient SharedHttp = new()
    {
        DefaultRequestHeaders = { { "User-Agent", "SnippingTool" } },
    };

    private readonly HttpClient _http;
    private readonly Action<string> _launchInstaller;
    private readonly ILogger<UpdateDownloadViewModel>? _logger;

    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _statusText = "Starting download…";
    [ObservableProperty] private bool _isDownloading = true;
    [ObservableProperty] private bool _isFailed;

    public event Action? RequestClose;

    public UpdateDownloadViewModel(ILogger<UpdateDownloadViewModel> logger)
        : this(SharedHttp, path => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }), logger) { }

    private UpdateDownloadViewModel(HttpClient http, Action<string> launchInstaller, ILogger<UpdateDownloadViewModel>? logger)
    {
        _http = http;
        _launchInstaller = launchInstaller;
        _logger = logger;
    }

    public static UpdateDownloadViewModel CreateForTesting(HttpClient http, Action<string>? launchInstaller = null) =>
        new(http, launchInstaller ?? (_ => { }), null);

    public async Task DownloadAndInstallAsync(string downloadUrl, string destPath, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            var buffer = new byte[81920];
            var downloaded = 0L;

            await using var src = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var dest = File.Create(destPath);

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

            ProgressPercent = 100;
            StatusText = "Download complete. Launching installer…";
            IsDownloading = false;

            _logger?.LogInformation("Update downloaded to {Path}", destPath);

            Process.Start(new ProcessStartInfo(destPath) { UseShellExecute = true });
            RequestClose?.Invoke();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Download cancelled.";
            IsDownloading = false;
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
    private void Cancel() => RequestClose?.Invoke();
}
