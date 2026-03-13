using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SnippingTool.Models;

namespace SnippingTool.Services;

public sealed class AutoUpdateService : BackgroundService, IAutoUpdateService
{
    private readonly IUpdateService _updateService;
    private readonly IUserSettingsService _userSettings;
    private readonly IUpdateDownloadService _downloadService;
    private readonly IMessageBoxService _messageBox;
    private readonly ILogger<AutoUpdateService> _logger;

    private SynchronizationContext? _uiContext;

    public event Action<UpdateCheckResult>? UpdateAvailable;

    public AutoUpdateService(
        IUpdateService updateService,
        IUserSettingsService userSettings,
        IUpdateDownloadService downloadService,
        IMessageBoxService messageBox,
        ILogger<AutoUpdateService> logger)
    {
        _updateService = updateService;
        _userSettings = userSettings;
        _downloadService = downloadService;
        _messageBox = messageBox;
        _logger = logger;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _uiContext = SynchronizationContext.Current;
        return base.StartAsync(cancellationToken);
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

        _userSettings.Current.LastAutoUpdateCheckUtc = DateTime.UtcNow;
        _userSettings.Save(_userSettings.Current);

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

                _userSettings.Current.LastAutoUpdateCheckUtc = DateTime.UtcNow;
                _userSettings.Save(_userSettings.Current);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Auto-update: periodic loop cancelled");
        }
    }

    public async Task ConfirmAndInstallAsync(UpdateCheckResult result)
    {
        var v = result.LatestVersion;
        if (!_messageBox.Confirm(
                $"Version {v.Major}.{v.Minor}.{v.Build} is available. Download and install now?",
                "Update Available"))
        {
            return;
        }

        var fileName = $"SnippingTool-Setup-{v.Major}.{v.Minor}.{v.Build}.exe";
        var destPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), fileName);
        var succeeded = await _downloadService.ShowAsync(result.DownloadUrl, destPath);
        if (succeeded)
        {
            System.Windows.Application.Current.Shutdown();
        }
    }

    private async Task CheckAndNotifyAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Auto-update: checking for updates");
        var result = await _updateService.CheckForUpdatesAsync(cancellationToken);
        if (result.IsUpdateAvailable)
        {
            _logger.LogInformation("Auto-update: update available ({Version})", result.LatestVersion);
            if (_uiContext is not null)
            {
                _uiContext.Post(_ => UpdateAvailable?.Invoke(result), null);
            }
            else
            {
                UpdateAvailable?.Invoke(result);
            }
        }
        else
        {
            _logger.LogDebug("Auto-update: already up to date");
        }
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
}
