using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SnippingTool.Models;
using SnippingTool.Services;
using Color = System.Windows.Media.Color;

namespace SnippingTool.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IUserSettingsService _settingsService;

    public SettingsViewModel(IUserSettingsService settingsService)
    {
        _settingsService = settingsService;

        var s = settingsService.Current;
        _screenshotSavePath = s.ScreenshotSavePath;
        _autoSaveScreenshots = s.AutoSaveScreenshots;
        _recordingOutputPath = s.RecordingOutputPath;
        _recordingFormat = s.RecordingFormat;
        _recordingFps = s.RecordingFps;
        _recordingJpegQuality = s.RecordingJpegQuality;
        _hudCloseDelaySeconds = s.HudCloseDelaySeconds;
        _captureDelaySeconds = s.CaptureDelaySeconds;
        _defaultStrokeThickness = s.DefaultStrokeThickness;
        _regionCaptureHotkey = s.RegionCaptureHotkey;

        try
        {
            _defaultAnnotationColor = (Color)System.Windows.Media.ColorConverter.ConvertFromString(s.DefaultAnnotationColor);
        }
        catch
        {
            _defaultAnnotationColor = Colors.Red;
        }
    }

    [ObservableProperty]
    private string _screenshotSavePath;

    [ObservableProperty]
    private bool _autoSaveScreenshots;

    [ObservableProperty]
    private string _recordingOutputPath;

    [ObservableProperty]
    private RecordingFormat _recordingFormat;

    [ObservableProperty]
    private int _recordingFps;

    [ObservableProperty]
    private int _recordingJpegQuality;

    [ObservableProperty]
    private int _hudCloseDelaySeconds;

    [ObservableProperty]
    private int _captureDelaySeconds;

    [ObservableProperty]
    private Color _defaultAnnotationColor;

    [ObservableProperty]
    private double _defaultStrokeThickness;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RegionCaptureHotkeyDisplayName))]
    private uint _regionCaptureHotkey;

    [ObservableProperty]
    private bool _isRecordingHotkey;

    public string RegionCaptureHotkeyDisplayName => VkToDisplayName(RegionCaptureHotkey);

    partial void OnDefaultAnnotationColorChanged(Color value) =>
        OnPropertyChanged(nameof(ColorPreviewBrush));

    public SolidColorBrush ColorPreviewBrush => new(DefaultAnnotationColor);

    public event Action? RequestClose;

    [RelayCommand]
    private void BrowseScreenshotPath()
    {
        var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select screenshot save folder",
            SelectedPath = ScreenshotSavePath
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            ScreenshotSavePath = dlg.SelectedPath;
        }
    }

    [RelayCommand]
    private void BrowseRecordingPath()
    {
        var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select recording output folder",
            SelectedPath = RecordingOutputPath
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            RecordingOutputPath = dlg.SelectedPath;
        }
    }

    [RelayCommand]
    private void PickAnnotationColor()
    {
        var cur = DefaultAnnotationColor;
        var dlg = new System.Windows.Forms.ColorDialog
        {
            Color = System.Drawing.Color.FromArgb(cur.A, cur.R, cur.G, cur.B),
            FullOpen = true
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            var c = dlg.Color;
            DefaultAnnotationColor = Color.FromArgb(c.A, c.R, c.G, c.B);
        }
    }

    [RelayCommand]
    private void Save()
    {
        var c = DefaultAnnotationColor;
        _settingsService.Save(new UserSettings
        {
            ScreenshotSavePath = ScreenshotSavePath,
            AutoSaveScreenshots = AutoSaveScreenshots,
            RecordingOutputPath = RecordingOutputPath,
            RecordingFormat = RecordingFormat,
            RecordingFps = RecordingFps,
            RecordingJpegQuality = RecordingJpegQuality,
            HudCloseDelaySeconds = HudCloseDelaySeconds,
            CaptureDelaySeconds = CaptureDelaySeconds,
            HudGapPixels = _settingsService.Current.HudGapPixels,
            DefaultAnnotationColor = $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}",
            DefaultStrokeThickness = DefaultStrokeThickness,
            RegionCaptureHotkey = RegionCaptureHotkey,
        });
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void StartRecordingHotkey() => IsRecordingHotkey = true;

    [RelayCommand]
    private void ResetHotkey()
    {
        RegionCaptureHotkey = 0x2C; // VK_SNAPSHOT (Print Screen)
        IsRecordingHotkey = false;
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke();

    private static string VkToDisplayName(uint vk) =>
        vk switch
        {
            0x2C => "Print Screen",
            _ => KeyInterop.KeyFromVirtualKey((int)vk).ToString(),
        };
}
