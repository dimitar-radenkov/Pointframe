using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pointframe.Services;

namespace Pointframe.ViewModels;

public partial class UpdateDownloadViewModel : ObservableObject
{
    internal static readonly HttpClient SharedHttp = new()
    {
        DefaultRequestHeaders = { { "User-Agent", "Pointframe" } },
    };

    private readonly HttpClient _http;
    private readonly IProcessService _process;
    private readonly ILogger<UpdateDownloadViewModel>? _logger;
    private CancellationTokenSource? _downloadCancellation;

    internal Func<string, bool> InstallerSignatureVerifier { get; set; }

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
        InstallerSignatureVerifier = VerifyInstallerSignature;
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
            StatusText = "Download complete. Verifying installer…";
            IsDownloading = false;

            _logger?.LogInformation("Update downloaded to {Path}", destPath);

            if (!InstallerSignatureVerifier(destPath))
            {
                StatusText = "Download failed: installer signature could not be verified.";
                IsFailed = true;
                TryDeleteFile(destPath);
                return;
            }

            StatusText = "Installer verified. Launching…";
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

    private bool VerifyInstallerSignature(string path)
    {
        try
        {
            // WinVerifyTrust is the correct Windows API for Authenticode (PE) verification.
            var result = NativeMethods.WinVerifyTrust(path);
            if (result == 0)
            {
                _logger?.LogInformation("Installer at '{Path}' passed Authenticode verification", path);
                return true;
            }

            _logger?.LogError(
                "Installer at '{Path}' failed Authenticode verification (WinVerifyTrust returned 0x{Code:X8})",
                path, result);
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger?.LogError(ex, "Authenticode check threw for installer at '{Path}'", path);
            return false;
        }
    }

    private static class NativeMethods
    {
        private static readonly Guid WinTrustActionGenericVerifyV2 =
            new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        internal static uint WinVerifyTrust(string filePath)
        {
            var fileInfo = new WinTrustFileInfo
            {
                CbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                PcwszFilePath = filePath,
                HFile = IntPtr.Zero,
                PgKnownSubject = IntPtr.Zero,
            };

            var pFileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            try
            {
                Marshal.StructureToPtr(fileInfo, pFileInfo, false);

                var data = new WinTrustData
                {
                    CbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                    DwUIChoice = 2,          // WTD_UI_NONE
                    FdwRevocationChecks = 0, // WTD_REVOKE_NONE
                    DwUnionChoice = 1,       // WTD_CHOICE_FILE
                    PFile = pFileInfo,
                    DwStateAction = 0,       // WTD_STATEACTION_IGNORE
                    DwProvFlags = 0x1040,    // WTD_REVOCATION_CHECK_NONE | WTD_SAFER_FLAG
                };

                var actionId = WinTrustActionGenericVerifyV2;
                return WinVerifyTrustNative(IntPtr.Zero, ref actionId, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(pFileInfo);
            }
        }

        [DllImport("wintrust.dll", EntryPoint = "WinVerifyTrust", ExactSpelling = true,
                   SetLastError = false, CharSet = CharSet.Unicode)]
        private static extern uint WinVerifyTrustNative(
            IntPtr hwnd, ref Guid pgActionID, ref WinTrustData pWVTData);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustFileInfo
        {
            public uint CbStruct;
            public string PcwszFilePath;
            public IntPtr HFile;
            public IntPtr PgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustData
        {
            public uint CbStruct;
            public IntPtr PPolicyCallbackData;
            public IntPtr PSIPClientData;
            public uint DwUIChoice;
            public uint FdwRevocationChecks;
            public uint DwUnionChoice;
            public nint PFile;
            public uint DwStateAction;
            public IntPtr HWVTStateData;
            public IntPtr PwszURLReference;
            public uint DwProvFlags;
            public uint DwUIContext;
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to delete untrusted installer at '{Path}'", path);
        }
    }
}
