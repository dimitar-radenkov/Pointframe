using System.Diagnostics;
using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;
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
        contextMenu.Items.Add(CreateOpenFoldersMenuItem());
        contextMenu.Items.Add(new WpfSeparator());
        contextMenu.Items.Add(CreateTrayMenuItem("Settings", Settings_Click));
        contextMenu.Items.Add(CreateTrayMenuItem("Check for Updates", CheckForUpdates_Click));
        contextMenu.Items.Add(CreateTrayMenuItem("About", About_Click));
        contextMenu.Items.Add(new WpfSeparator());
        contextMenu.Items.Add(CreateTrayMenuItem("Exit", Exit_Click));
        return contextMenu;
    }

    private WpfMenuItem CreateOpenFoldersMenuItem()
    {
        var openFoldersMenuItem = new WpfMenuItem
        {
            Header = "Open folders",
        };

        openFoldersMenuItem.Items.Add(CreateTrayMenuItem("Snips folder", OpenSnipsFolder_Click));
        openFoldersMenuItem.Items.Add(CreateTrayMenuItem("Videos folder", OpenVideosFolder_Click));
        openFoldersMenuItem.Items.Add(CreateTrayMenuItem("Logs folder", OpenLogsFolder_Click));
        return openFoldersMenuItem;
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

    private void OpenSnipsFolder_Click(object sender, RoutedEventArgs e)
    {
        OpenConfiguredFolder(_userSettings.Current.ScreenshotSavePath);
    }

    private void OpenVideosFolder_Click(object sender, RoutedEventArgs e)
    {
        OpenConfiguredFolder(_userSettings.Current.RecordingOutputPath);
    }

    private void OpenLogsFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.LogsDirectory);
        OpenFolder(AppPaths.LogsDirectory);
    }

    private void ClearRecentCaptures_Click(object sender, RoutedEventArgs e)
    {
        _recentCaptures.Clear();
        RebuildRecentCapturesMenu();
    }

    private void ClearRecentRecordings_Click(object sender, RoutedEventArgs e)
    {
        _recentRecordings.Clear();
        RebuildRecentRecordingsMenu();
    }

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

        contextMenu.Items.Insert(3, _recentCapturesMenuItem);
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
            _recentCapturesMenuItem.Items.Add(new WpfSeparator());
            var openFolder = CreateTrayMenuItem("Open Snips folder", OpenSnipsFolder_Click);
            _recentCapturesMenuItem.Items.Add(openFolder);
            return;
        }

        foreach (var capturePath in _recentCaptures)
        {
            var fileName = Path.GetFileName(capturePath);
            var panel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            };
            var textBlock = new System.Windows.Controls.TextBlock
            {
                Text = fileName,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                Margin = new System.Windows.Thickness(0, 0, 8, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = capturePath,
            };
            textBlock.MouseLeftButtonDown += (_, args) =>
            {
                OpenRecentCapture_Click(textBlock, new RoutedEventArgs());
                args.Handled = true;
            };

            var button = CreateRecentActionButton("📁", "Open folder", capturePath, "Open capture folder");
            System.Windows.Automation.AutomationProperties.SetName(button, "Open capture folder");
            button.Click += (_, args) =>
            {
                OpenRecentCaptureFolder_Click(button, new RoutedEventArgs());
                args.Handled = true;
            };

            panel.Children.Add(textBlock);
            panel.Children.Add(button);

            var menuItem = new WpfMenuItem
            {
                Header = panel,
                Tag = capturePath,
            };
            menuItem.Click += OpenRecentCapture_Click;
            _recentCapturesMenuItem.Items.Add(menuItem);
        }

        _recentCapturesMenuItem.Items.Add(new WpfSeparator());
        var clearRecent = CreateTrayMenuItem("Clear recent captures", ClearRecentCaptures_Click);
        _recentCapturesMenuItem.Items.Add(clearRecent);
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

        contextMenu.Items.Insert(4, _recentRecordingsMenuItem);
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
            _recentRecordingsMenuItem.Items.Add(new WpfSeparator());
            var openFolder = CreateTrayMenuItem("Open Videos folder", OpenVideosFolder_Click);
            _recentRecordingsMenuItem.Items.Add(openFolder);
            return;
        }

        foreach (var recentRecording in _recentRecordings)
        {
            var fileName = $"{recentRecording.FileName} ({recentRecording.ElapsedText})";
            var panel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            };
            var textBlock = new System.Windows.Controls.TextBlock
            {
                Text = fileName,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                Margin = new System.Windows.Thickness(0, 0, 6, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = recentRecording,
            };
            textBlock.MouseLeftButtonDown += (_, args) =>
            {
                OpenRecentRecording_Click(textBlock, new RoutedEventArgs());
                args.Handled = true;
            };

            var gifButton = CreateRecentActionButton(
                "🎬",
                "Export to GIF",
                recentRecording,
                "Export recording to GIF",
                new System.Windows.Thickness(0, 0, 4, 0));
            gifButton.Click += (_, args) =>
            {
                ExportRecentRecordingGif_Click(gifButton, new RoutedEventArgs());
                args.Handled = true;
            };

            var folderButton = CreateRecentActionButton("📁", "Open folder", recentRecording, "Open recording folder");
            folderButton.Click += (_, args) =>
            {
                OpenRecentRecordingFolder_Click(folderButton, new RoutedEventArgs());
                args.Handled = true;
            };

            panel.Children.Add(textBlock);
            panel.Children.Add(gifButton);
            panel.Children.Add(folderButton);

            var menuItem = new WpfMenuItem
            {
                Header = panel,
                Tag = recentRecording,
            };
            menuItem.Click += OpenRecentRecording_Click;
            _recentRecordingsMenuItem.Items.Add(menuItem);
        }

        _recentRecordingsMenuItem.Items.Add(new WpfSeparator());
        var clearRecent = CreateTrayMenuItem("Clear recent recordings", ClearRecentRecordings_Click);
        _recentRecordingsMenuItem.Items.Add(clearRecent);
    }

    private void OpenRecentCapture_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetTaggedValue(sender, out string capturePath))
        {
            return;
        }

        OpenPath(capturePath);
    }

    private void OpenRecentCaptureFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetTaggedValue(sender, out string capturePath))
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
        if (!TryGetTaggedValue(sender, out RecentRecordingItem recentRecording))
        {
            return;
        }

        OpenPath(recentRecording.OutputPath);
    }

    private void OpenRecentRecordingFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetTaggedValue(sender, out RecentRecordingItem recentRecording))
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
        if (!TryGetTaggedValue(sender, out RecentRecordingItem recentRecording) || sender is not UIElement senderElement)
        {
            return;
        }

        if (!File.Exists(recentRecording.OutputPath))
        {
            _messageBox.ShowWarning("The recording file could not be found.", "Export to GIF");
            return;
        }

        var gifPath = Path.ChangeExtension(recentRecording.OutputPath, ".gif");
        senderElement.IsEnabled = false;
        _telemetry.TrackEvent("gif_export_started");

        var sw = Stopwatch.StartNew();
        var success = true;
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
            success = false;
            _logger.LogError(ex, "GIF export from recent recordings failed for {Path}", recentRecording.OutputPath);
            _telemetry.TrackException(ex, "gif_export");
            _messageBox.ShowWarning("The GIF export failed. Please try again.", "Export to GIF");
        }
        finally
        {
            sw.Stop();
            _telemetry.TrackEvent("gif_export_completed", new Dictionary<string, string>
            {
                ["success"] = success ? "true" : "false",
                ["duration_seconds"] = ((int)sw.Elapsed.TotalSeconds).ToString(),
            });
            senderElement.IsEnabled = true;
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
        _processService.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\""));
    }

    private void OpenConfiguredFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(path);
            OpenFolder(path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open configured folder: {Path}", path);
            _messageBox.ShowWarning(
                "Could not open the configured folder. Please verify the path in Settings.",
                "Open Folder");
        }
    }

    private static bool TryGetTaggedValue<T>(object sender, out T value)
    {
        if (sender is FrameworkElement { Tag: T taggedValue })
        {
            value = taggedValue;
            return true;
        }

        value = default!;
        return false;
    }

    private static System.Windows.Controls.Button CreateRecentActionButton(
        string content,
        string tooltip,
        object tag,
        string automationName,
        System.Windows.Thickness? margin = null)
    {
        var button = new System.Windows.Controls.Button
        {
            Content = content,
            Width = 32,
            Height = 28,
            Padding = new System.Windows.Thickness(4),
            ToolTip = tooltip,
            Tag = tag,
            Cursor = System.Windows.Input.Cursors.Hand,
            Margin = margin ?? new System.Windows.Thickness(0),
            Opacity = 0.88,
        };

        button.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, System.Windows.SystemColors.ControlLightBrushKey);
        button.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, System.Windows.SystemColors.ActiveBorderBrushKey);
        button.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, System.Windows.SystemColors.ControlTextBrushKey);
        System.Windows.Automation.AutomationProperties.SetName(button, automationName);

        button.MouseEnter += (_, _) => button.Opacity = 1.0;
        button.MouseLeave += (_, _) => button.Opacity = 0.88;
        return button;
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
