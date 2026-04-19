using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SnippingTool.Models;
using SnippingTool.Services.Messaging;

namespace SnippingTool.Services;

public sealed class AutoUpdateService : BackgroundService, IAutoUpdateService
{
    private readonly IEventAggregator _eventAggregator;
    private readonly IUpdateService _updateService;
    private readonly IUserSettingsService _userSettings;
    private readonly IUpdateDownloadService _downloadService;
    private readonly IMessageBoxService _messageBox;
    private readonly ILogger<AutoUpdateService> _logger;

    public AutoUpdateService(
        IEventAggregator eventAggregator,
        IUpdateService updateService,
        IUserSettingsService userSettings,
        IUpdateDownloadService downloadService,
        IMessageBoxService messageBox,
        ILogger<AutoUpdateService> logger)
    {
        _eventAggregator = eventAggregator;
        _updateService = updateService;
        _userSettings = userSettings;
        _downloadService = downloadService;
        _messageBox = messageBox;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Auto-update: running startup check");
        try
        {
            await CheckAndNotifyAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-update: startup check failed");
        }

        UpdateLastCheckedUtc();

        var interval = _userSettings.Current.AutoUpdateCheckInterval;
        if (interval == UpdateCheckInterval.Never)
        {
            _logger.LogInformation("Auto-update: periodic loop not started (interval = Never)");
            return;
        }

        var timerInterval = GetTimerInterval(interval);
        _logger.LogInformation(
            "Auto-update: periodic loop started (interval = {Interval}, every {IntervalTime})",
            interval,
            timerInterval);

        try
        {
            using var timer = new PeriodicTimer(timerInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (_userSettings.Current.AutoUpdateCheckInterval == UpdateCheckInterval.Never)
                {
                    _logger.LogInformation("Auto-update: periodic loop stopped (interval changed to Never)");
                    break;
                }

                _logger.LogInformation("Auto-update: running periodic check");
                try
                {
                    await CheckAndNotifyAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Auto-update: periodic check failed");
                }

                UpdateLastCheckedUtc();
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Auto-update: periodic loop cancelled");
        }
    }

    public async Task ConfirmAndInstall(UpdateCheckResult result)
    {
        var v = result.LatestVersion;
        if (!_messageBox.Confirm(
                $"Version {v.Major}.{v.Minor}.{v.Build} is available. Download and install now?",
                "Update Available"))
        {
            return;
        }

        var fileName = ResolveInstallerFileName(result.DownloadUrl, v);
        var destPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), fileName);
        var succeeded = await _downloadService.Show(result.DownloadUrl, destPath);
        if (succeeded)
        {
            _logger.LogInformation(
                "Update installer launched from {Path}; leaving application running for installer handoff",
                destPath);
        }
    }

    private async Task CheckAndNotifyAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Auto-update: checking for updates");
        var result = await _updateService.CheckForUpdates(cancellationToken);
        if (result.IsUpdateAvailable)
        {
            _logger.LogInformation("Auto-update: update available ({Version})", result.LatestVersion);
            await _eventAggregator.Publish(new UpdateAvailableMessage(result));
        }
        else
        {
            _logger.LogDebug("Auto-update: already up to date");
        }
    }

    private void UpdateLastCheckedUtc()
    {
        _userSettings.Update(settings =>
        {
            settings.LastAutoUpdateCheckUtc = DateTime.UtcNow;
        });
    }

    private static TimeSpan GetTimerInterval(UpdateCheckInterval interval) =>
        interval switch
        {
            UpdateCheckInterval.EveryDay => TimeSpan.FromDays(1),
            UpdateCheckInterval.EveryTwoDays => TimeSpan.FromDays(2),
            UpdateCheckInterval.EveryThreeDays => TimeSpan.FromDays(3),
#if DEBUG
            UpdateCheckInterval.EveryThirtySeconds => TimeSpan.FromSeconds(30),
#endif
            _ => TimeSpan.FromDays(1),
        };

    private static string ResolveInstallerFileName(string downloadUrl, Version version)
    {
        if (Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri))
        {
            var fileName = System.IO.Path.GetFileName(uri.LocalPath);
            if (!string.IsNullOrWhiteSpace(fileName) && fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return fileName;
            }
        }

        return $"Pointframe-{version.Major}.{version.Minor}.{version.Build}-Setup.exe";
    }
}
