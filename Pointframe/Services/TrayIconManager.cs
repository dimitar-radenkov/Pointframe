using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Extensions.Logging;
using Pointframe.Models;
using WpfApplication = System.Windows.Application;
using WpfContextMenu = System.Windows.Controls.ContextMenu;
using WpfMenuItem = System.Windows.Controls.MenuItem;
using WpfSeparator = System.Windows.Controls.Separator;

namespace Pointframe.Services;

internal sealed class TrayIconManager : ITrayIconManager
{
    private readonly ILogger<TrayIconManager> _logger;
    private readonly IMessageBoxService _messageBox;
    private readonly IProcessService _processService;
    private readonly IUpdateService _updateService;
    private readonly IAppVersionService _appVersionService;
    private readonly IAutoUpdateService _autoUpdate;
    private readonly IUserSettingsService _userSettings;
    private readonly IGifExportService _gifExportService;
    private readonly ITelemetryService _telemetry;
    private readonly Action _onNewSnip;
    private readonly Action _onWholeScreenSnip;
    private readonly Action _onOpenImage;
    private readonly Action _onShowSettings;
    private readonly Action _onShowAbout;

    private const int MaxRecentItems = 5;

    private TaskbarIcon? _trayIcon;
    private WpfMenuItem? _recentRecordingsMenuItem;
    private WpfMenuItem? _recentCapturesMenuItem;
    private UpdateCheckResult? _pendingUpdate;
    private string? _pendingRecordingBalloonPath;
    private readonly List<RecentRecordingItem> _recentRecordings = [];
    private readonly List<string> _recentCaptures = [];

    public TrayIconManager(
        ILogger<TrayIconManager> logger,
        IMessageBoxService messageBox,
        IProcessService processService,
        IUpdateService updateService,
        IAppVersionService appVersionService,
        IAutoUpdateService autoUpdate,
        IUserSettingsService userSettings,
        IGifExportService gifExportService,
        ITelemetryService telemetry,
        Action onNewSnip,
        Action onWholeScreenSnip,
        Action onOpenImage,
        Action onShowSettings,
        Action onShowAbout)
    {
        _logger = logger;
        _messageBox = messageBox;
        _processService = processService;
        _updateService = updateService;
        _appVersionService = appVersionService;
        _autoUpdate = autoUpdate;
        _userSettings = userSettings;
        _gifExportService = gifExportService;
        _telemetry = telemetry;
        _onNewSnip = onNewSnip;
        _onWholeScreenSnip = onWholeScreenSnip;
        _onOpenImage = onOpenImage;
        _onShowSettings = onShowSettings;
        _onShowAbout = onShowAbout;
    }

    public void Initialize()
    {
        _trayIcon = new TaskbarIcon
        {
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/icon.ico", UriKind.Absolute)),
            ToolTipText = "Pointframe",
            ContextMenu = CreateTrayContextMenu(),
        };
        _trayIcon.TrayLeftMouseUp += TrayIcon_LeftClick;
        _trayIcon.TrayBalloonTipClicked += OnTrayBalloonClicked;

        InitializeRecentCapturesMenu();
        InitializeRecentRecordingsMenu();
    }

    public void HandleUpdateAvailable(UpdateCheckResult result)
    {
        _pendingRecordingBalloonPath = null;
        _pendingUpdate = result;
        var v = result.LatestVersion;

        _trayIcon?.ShowBalloonTip(
            "Update Available",
            $"Version {v.Major}.{v.Minor}.{v.Build} is ready to download.",
            BalloonIcon.Info);
    }

    public void HandleRecordingCompleted(string outputPath, string elapsedText)
    {
        var recentRecording = new RecentRecordingItem(outputPath, elapsedText);
        _recentRecordings.RemoveAll(item => string.Equals(item.OutputPath, recentRecording.OutputPath, StringComparison.OrdinalIgnoreCase));
        _recentRecordings.Insert(0, recentRecording);
        if (_recentRecordings.Count > MaxRecentItems)
        {
            _recentRecordings.RemoveRange(MaxRecentItems, _recentRecordings.Count - MaxRecentItems);
        }

        RebuildRecentRecordingsMenu();
        ShowRecordingCompletedBalloon(recentRecording);
    }

    public void HandleCaptureCompleted(string outputPath)
    {
        _recentCaptures.RemoveAll(p => string.Equals(p, outputPath, StringComparison.OrdinalIgnoreCase));
        _recentCaptures.Insert(0, outputPath);
        if (_recentCaptures.Count > MaxRecentItems)
        {
            _recentCaptures.RemoveRange(MaxRecentItems, _recentCaptures.Count - MaxRecentItems);
        }

        RebuildRecentCapturesMenu();
    }

    public void AddDebugMenuItems()
    {
        if (_trayIcon?.ContextMenu is not { } contextMenu)
        {
            return;
        }

        var simulateUiErrorMenuItem = new WpfMenuItem
        {
            Header = "Simulate UI Error",
            InputGestureText = "Ctrl+Shift+F12"
        };
        simulateUiErrorMenuItem.Click += SimulateUiError_Click;
        contextMenu.Items.Insert(Math.Max(0, contextMenu.Items.Count - 1), simulateUiErrorMenuItem);
    }

    public void Dispose()
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
    }

    private WpfContextMenu CreateTrayContextMenu()
    {
        var contextMenu = new WpfContextMenu();
        contextMenu.Items.Add(CreateTrayMenuItem("New Snip", NewSnip_Click));
        contextMenu.Items.Add(CreateTrayMenuItem("Whole screen snip", WholeScreenSnip_Click));
        contextMenu.Items.Add(CreateTrayMenuItem("Open image...", OpenImage_Click));
        contextMenu.Items.Add(new WpfSeparator());
        contextMenu.Items.Add(CreateTrayMenuItem("Settings", Settings_Click));
        contextMenu.Items.Add(CreateTrayMenuItem("Check for Updates", CheckForUpdates_Click));
        contextMenu.Items.Add(CreateTrayMenuItem("About", About_Click));
        contextMenu.Items.Add(new WpfSeparator());
        contextMenu.Items.Add(CreateTrayMenuItem("Exit", Exit_Click));
        return contextMenu;
    }

    internal static WpfMenuItem CreateTrayMenuItem(string header, RoutedEventHandler clickHandler)
    {
        var menuItem = new WpfMenuItem
        {
            Header = header,
        };
        menuItem.Click += clickHandler;
        return menuItem;
    }

    private void TrayIcon_LeftClick(object sender, RoutedEventArgs e) => _onNewSnip();
    private void NewSnip_Click(object sender, RoutedEventArgs e) => _onNewSnip();
    private void WholeScreenSnip_Click(object sender, RoutedEventArgs e) => _onWholeScreenSnip();
    private void Settings_Click(object sender, RoutedEventArgs e) => _onShowSettings();
    private void About_Click(object sender, RoutedEventArgs e) => _onShowAbout();
    private void OpenImage_Click(object sender, RoutedEventArgs e) => _onOpenImage();
    private void Exit_Click(object sender, RoutedEventArgs e) => WpfApplication.Current.Shutdown();

    private void InitializeRecentCapturesMenu()
    {
        if (_trayIcon?.ContextMenu is not { } contextMenu)
        {
            return;
        }

        _recentCapturesMenuItem = new WpfMenuItem
        {
            Header = "Recent captures",
        };

        contextMenu.Items.Insert(2, _recentCapturesMenuItem);
        RebuildRecentCapturesMenu();
    }

    private void RebuildRecentCapturesMenu()
    {
        if (_recentCapturesMenuItem is null)
        {
            return;
        }

        _recentCapturesMenuItem.Items.Clear();

        if (_recentCaptures.Count == 0)
        {
            _recentCapturesMenuItem.Items.Add(new WpfMenuItem
            {
                Header = "No recent captures",
                IsEnabled = false,
            });
            return;
        }

        foreach (var capturePath in _recentCaptures)
        {
            var captureItem = new WpfMenuItem
            {
                Header = Path.GetFileName(capturePath),
            };
            captureItem.Items.Add(CreateRecentCaptureActionMenuItem("Open", OpenRecentCapture_Click, capturePath));
            captureItem.Items.Add(CreateRecentCaptureActionMenuItem("Open folder", OpenRecentCaptureFolder_Click, capturePath));
            _recentCapturesMenuItem.Items.Add(captureItem);
        }
    }

    private static WpfMenuItem CreateRecentCaptureActionMenuItem(
        string header,
        RoutedEventHandler clickHandler,
        string capturePath)
    {
        var menuItem = new WpfMenuItem
        {
            Header = header,
            Tag = capturePath,
        };
        menuItem.Click += clickHandler;
        return menuItem;
    }

    private void InitializeRecentRecordingsMenu()
    {
        if (_trayIcon?.ContextMenu is not { } contextMenu)
        {
            return;
        }

        _recentRecordingsMenuItem = new WpfMenuItem
        {
            Header = "Recent recordings",
        };

        contextMenu.Items.Insert(3, _recentRecordingsMenuItem);
        RebuildRecentRecordingsMenu();
    }

    private void RebuildRecentRecordingsMenu()
    {
        if (_recentRecordingsMenuItem is null)
        {
            return;
        }

        _recentRecordingsMenuItem.Items.Clear();

        if (_recentRecordings.Count == 0)
        {
            _recentRecordingsMenuItem.Items.Add(new WpfMenuItem
            {
                Header = "No recent recordings",
                IsEnabled = false,
            });
            return;
        }

        foreach (var recentRecording in _recentRecordings)
        {
            var recentRecordingItem = new WpfMenuItem
            {
                Header = $"{recentRecording.FileName} ({recentRecording.ElapsedText})",
            };
            recentRecordingItem.Items.Add(CreateRecentRecordingActionMenuItem("Open", OpenRecentRecording_Click, recentRecording));
            recentRecordingItem.Items.Add(CreateRecentRecordingActionMenuItem("Open folder", OpenRecentRecordingFolder_Click, recentRecording));
            recentRecordingItem.Items.Add(CreateRecentRecordingActionMenuItem("Export to GIF", ExportRecentRecordingGif_Click, recentRecording));
            _recentRecordingsMenuItem.Items.Add(recentRecordingItem);
        }
    }

    private static WpfMenuItem CreateRecentRecordingActionMenuItem(
        string header,
        RoutedEventHandler clickHandler,
        RecentRecordingItem recentRecording)
    {
        var menuItem = new WpfMenuItem
        {
            Header = header,
            Tag = recentRecording,
        };
        menuItem.Click += clickHandler;
        return menuItem;
    }

    private void OpenRecentCapture_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfMenuItem { Tag: string capturePath })
        {
            return;
        }

        OpenPath(capturePath);
    }

    private void OpenRecentCaptureFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfMenuItem { Tag: string capturePath })
        {
            return;
        }

        var directory = Path.GetDirectoryName(capturePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            OpenFolder(directory);
        }
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        var menuItem = (WpfMenuItem)sender;
        menuItem.IsEnabled = false;
        _telemetry.TrackEvent("update_check_manual");

        try
        {
            var result = await _updateService.CheckForUpdates();

            if (!result.IsUpdateAvailable)
            {
                var current = _appVersionService.Current;
                _messageBox.ShowInformation(
                    $"You're already on the latest version (v{current.Major}.{current.Minor}.{current.Build}).",
                    "Check for Updates");
                return;
            }

            await _autoUpdate.ConfirmAndInstall(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update check failed");
            _messageBox.ShowWarning(
                "Could not check for updates. Please check your internet connection and try again.",
                "Check for Updates");
        }
        finally
        {
            menuItem.IsEnabled = true;
        }
    }

    private void OpenRecentRecording_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfMenuItem { Tag: RecentRecordingItem recentRecording })
        {
            return;
        }

        OpenPath(recentRecording.OutputPath);
    }

    private void OpenRecentRecordingFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfMenuItem { Tag: RecentRecordingItem recentRecording })
        {
            return;
        }

        var directory = Path.GetDirectoryName(recentRecording.OutputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            OpenFolder(directory);
        }
    }

    private async void ExportRecentRecordingGif_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfMenuItem { Tag: RecentRecordingItem recentRecording } menuItem)
        {
            return;
        }

        if (!File.Exists(recentRecording.OutputPath))
        {
            _messageBox.ShowWarning("The recording file could not be found.", "Export to GIF");
            return;
        }

        var gifPath = Path.ChangeExtension(recentRecording.OutputPath, ".gif");
        menuItem.IsEnabled = false;
        _telemetry.TrackEvent("gif_export_started");

        try
        {
            await _gifExportService.Export(recentRecording.OutputPath, gifPath, _userSettings.Current.GifFps).ConfigureAwait(true);
            var directory = Path.GetDirectoryName(gifPath) ?? gifPath;
            _trayIcon?.ShowBalloonTip(
                "GIF exported",
                $"{Path.GetFileName(gifPath)} is ready.{Environment.NewLine}{directory}",
                BalloonIcon.Info);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GIF export from recent recordings failed for {Path}", recentRecording.OutputPath);
            _telemetry.TrackException(ex, "gif_export");
            _messageBox.ShowWarning("The GIF export failed. Please try again.", "Export to GIF");
        }
        finally
        {
            menuItem.IsEnabled = true;
        }
    }

    private async void OnTrayBalloonClicked(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is not null)
        {
            var update = _pendingUpdate;
            _pendingUpdate = null;
            await _autoUpdate.ConfirmAndInstall(update);
            return;
        }

        if (string.IsNullOrWhiteSpace(_pendingRecordingBalloonPath))
        {
            return;
        }

        OpenPath(_pendingRecordingBalloonPath);
        _pendingRecordingBalloonPath = null;
    }

    private void ShowRecordingCompletedBalloon(RecentRecordingItem recentRecording)
    {
        _pendingRecordingBalloonPath = recentRecording.OutputPath;
        var directory = Path.GetDirectoryName(recentRecording.OutputPath) ?? recentRecording.OutputPath;
        _trayIcon?.ShowBalloonTip(
            "Recording saved",
            $"{recentRecording.FileName} • {recentRecording.ElapsedText}{Environment.NewLine}{directory}",
            BalloonIcon.Info);
    }

    private void OpenPath(string path)
    {
        if (!File.Exists(path))
        {
            _messageBox.ShowWarning("The selected recording file could not be found.", "Open Recording");
            return;
        }

        _processService.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true,
        });
    }

    private void OpenFolder(string path)
    {
        _processService.Start(new ProcessStartInfo("explorer.exe", path));
    }

    private void SimulateUiError_Click(object sender, RoutedEventArgs e)
    {
        _logger.LogDebug("Simulating UI recovery smoke test from tray menu");
        throw new InvalidOperationException("Debug-only UI recovery smoke test.");
    }

    internal sealed record RecentRecordingItem(string OutputPath, string ElapsedText)
    {
        public string FileName => Path.GetFileName(OutputPath);
    }
}
