using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SnippingTool.Models;
using SnippingTool.Services;

namespace SnippingTool.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private const double MinRecordingCursorHighlightSize = 8d;
    private const double MaxRecordingCursorHighlightSize = 96d;

    private readonly IDialogService _dialogService;
    private readonly IUserSettingsService _settingsService;
    private readonly IThemeService _themeService;
    private readonly AppTheme _originalTheme;

    public SettingsViewModel(IUserSettingsService settingsService, IThemeService themeService, IDialogService dialogService)
    {
        _dialogService = dialogService;
        _settingsService = settingsService;
        _themeService = themeService;

        var s = settingsService.Current;
        _screenshotSavePath = s.ScreenshotSavePath;
        _autoSaveScreenshots = s.AutoSaveScreenshots;
        _recordingOutputPath = s.RecordingOutputPath;
        _recordingFormat = s.RecordingFormat;
        _gifFps = s.GifFps;
        _hudCloseDelaySeconds = s.HudCloseDelaySeconds;
        _recordingCursorHighlightEnabled = s.RecordingCursorHighlightEnabled;
        _recordingClickRippleEnabled = s.RecordingClickRippleEnabled;
        _recordingCursorHighlightSize = ClampRecordingCursorHighlightSize(s.RecordingCursorHighlightSize);
        _captureDelaySeconds = s.CaptureDelaySeconds;
        _defaultStrokeThickness = s.DefaultStrokeThickness;
        _regionCaptureHotkey = s.RegionCaptureHotkey;
        _autoUpdateCheckInterval = s.AutoUpdateCheckInterval;
        _appTheme = s.Theme;
        _originalTheme = s.Theme;

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
    private int _gifFps;

    [ObservableProperty]
    private int _hudCloseDelaySeconds;

    [ObservableProperty]
    private bool _recordingCursorHighlightEnabled;

    [ObservableProperty]
    private bool _recordingClickRippleEnabled;

    [ObservableProperty]
    private double _recordingCursorHighlightSize;

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

    [ObservableProperty]
    private UpdateCheckInterval _autoUpdateCheckInterval;

    [ObservableProperty]
    private AppTheme _appTheme;

    public string RegionCaptureHotkeyDisplayName => VkToDisplayName(RegionCaptureHotkey);

    partial void OnDefaultAnnotationColorChanged(Color value) =>
        OnPropertyChanged(nameof(ColorPreviewBrush));

    partial void OnAppThemeChanged(AppTheme value) => _themeService.Apply(value);

    public SolidColorBrush ColorPreviewBrush => new(DefaultAnnotationColor);

    public event Action? RequestClose;

    [RelayCommand]
    private void BrowseScreenshotPath()
    {
        var selectedPath = _dialogService.PickFolder(ScreenshotSavePath, "Select screenshot save folder");
        if (!string.IsNullOrWhiteSpace(selectedPath))
        {
            ScreenshotSavePath = selectedPath;
        }
    }

    [RelayCommand]
    private void BrowseRecordingPath()
    {
        var selectedPath = _dialogService.PickFolder(RecordingOutputPath, "Select recording output folder");
        if (!string.IsNullOrWhiteSpace(selectedPath))
        {
            RecordingOutputPath = selectedPath;
        }
    }

    [RelayCommand]
    private void PickAnnotationColor()
    {
        var selectedColor = _dialogService.PickColor(DefaultAnnotationColor);
        if (selectedColor.HasValue)
        {
            DefaultAnnotationColor = selectedColor.Value;
        }
    }

    [RelayCommand]
    private void Save()
    {
        var c = DefaultAnnotationColor;
        var clampedRecordingCursorHighlightSize = ClampRecordingCursorHighlightSize(RecordingCursorHighlightSize);
        RecordingCursorHighlightSize = clampedRecordingCursorHighlightSize;

        _settingsService.Save(new UserSettings
        {
            ScreenshotSavePath = ScreenshotSavePath,
            AutoSaveScreenshots = AutoSaveScreenshots,
            RecordingOutputPath = RecordingOutputPath,
            RecordingFormat = RecordingFormat,
            RecordingFps = _settingsService.Current.RecordingFps,
            RecordingJpegQuality = _settingsService.Current.RecordingJpegQuality,
            GifFps = GifFps,
            HudCloseDelaySeconds = HudCloseDelaySeconds,
            RecordingCursorHighlightEnabled = RecordingCursorHighlightEnabled,
            RecordingClickRippleEnabled = RecordingClickRippleEnabled,
            RecordingCursorHighlightSize = clampedRecordingCursorHighlightSize,
            CaptureDelaySeconds = CaptureDelaySeconds,
            HudGapPixels = _settingsService.Current.HudGapPixels,
            DefaultAnnotationColor = $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}",
            DefaultStrokeThickness = DefaultStrokeThickness,
            RegionCaptureHotkey = RegionCaptureHotkey,
            AutoUpdateCheckInterval = AutoUpdateCheckInterval,
            LastAutoUpdateCheckUtc = _settingsService.Current.LastAutoUpdateCheckUtc,
            Theme = AppTheme,
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
    private void Cancel()
    {
        _themeService.Apply(_originalTheme);
        RequestClose?.Invoke();
    }

    internal void RevertThemePreview() => _themeService.Apply(_originalTheme);

    private static string VkToDisplayName(uint vk) =>
        vk switch
        {
            0x2C => "Print Screen",
            _ => KeyInterop.KeyFromVirtualKey((int)vk).ToString(),
        };

    private static double ClampRecordingCursorHighlightSize(double size)
    {
        return Math.Clamp(size, MinRecordingCursorHighlightSize, MaxRecordingCursorHighlightSize);
    }
}
