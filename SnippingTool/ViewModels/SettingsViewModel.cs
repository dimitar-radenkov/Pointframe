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
    private static readonly SettingsSectionItem[] SectionItems =
    [
        new(SettingsSection.Capture, "Capture", "Screenshot folders, timing, and the capture shortcut."),
        new(SettingsSection.Recording, "Recording", "Output options, cursor effects, and advanced recording defaults."),
        new(SettingsSection.Annotation, "Annotation", "Default annotation appearance and preview."),
        new(SettingsSection.App, "App", "Appearance, update checks, and reset actions."),
    ];

    private readonly IDialogService _dialogService;
    private readonly IUserSettingsService _settingsService;
    private readonly IThemeService _themeService;
    private readonly AppTheme _originalTheme;
    private int _recordingFps;
    private int _recordingJpegQuality;
    private int _hudGapPixels;
    private DateTime? _lastAutoUpdateCheckUtc;

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
        _recordingCursorHighlightEnabled = s.RecordingCursorHighlightEnabled;
        _recordingClickRippleEnabled = s.RecordingClickRippleEnabled;
        _recordingCursorHighlightSize = ClampRecordingCursorHighlightSize(s.RecordingCursorHighlightSize);
        _captureDelaySeconds = s.CaptureDelaySeconds;
        _defaultStrokeThickness = s.DefaultStrokeThickness;
        _regionCaptureHotkey = s.RegionCaptureHotkey;
        _autoUpdateCheckInterval = s.AutoUpdateCheckInterval;
        _appTheme = s.Theme;
        _originalTheme = s.Theme;
        _recordingFps = s.RecordingFps;
        _recordingJpegQuality = s.RecordingJpegQuality;
        _hudGapPixels = s.HudGapPixels;
        _lastAutoUpdateCheckUtc = s.LastAutoUpdateCheckUtc;

        _defaultAnnotationColor = ParseAnnotationColorOrFallback(s.DefaultAnnotationColor);
    }

    public IReadOnlyList<SettingsSectionItem> Sections => SectionItems;

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
    [NotifyPropertyChangedFor(nameof(AnnotationPreviewThickness))]
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedSectionItem))]
    [NotifyPropertyChangedFor(nameof(SelectedSectionDisplayName))]
    [NotifyPropertyChangedFor(nameof(SelectedSectionDescription))]
    [NotifyPropertyChangedFor(nameof(IsCaptureSectionSelected))]
    [NotifyPropertyChangedFor(nameof(IsRecordingSectionSelected))]
    [NotifyPropertyChangedFor(nameof(IsAnnotationSectionSelected))]
    [NotifyPropertyChangedFor(nameof(IsAppSectionSelected))]
    private SettingsSection _selectedSection = SettingsSection.Capture;

    public SettingsSectionItem SelectedSectionItem =>
        Array.Find(SectionItems, item => item.Section == SelectedSection) ?? SectionItems[0];

    public string RegionCaptureHotkeyDisplayName => VkToDisplayName(RegionCaptureHotkey);
    public string SelectedSectionDisplayName => SelectedSectionItem.DisplayName;
    public string SelectedSectionDescription => SelectedSectionItem.Description;
    public bool IsCaptureSectionSelected => SelectedSection == SettingsSection.Capture;
    public bool IsRecordingSectionSelected => SelectedSection == SettingsSection.Recording;
    public bool IsAnnotationSectionSelected => SelectedSection == SettingsSection.Annotation;
    public bool IsAppSectionSelected => SelectedSection == SettingsSection.App;

    partial void OnDefaultAnnotationColorChanged(Color value) =>
        OnPropertyChanged(nameof(ColorPreviewBrush));

    partial void OnAppThemeChanged(AppTheme value) => _themeService.Apply(value);

    public SolidColorBrush ColorPreviewBrush => new(DefaultAnnotationColor);
    public double AnnotationPreviewThickness => Math.Max(DefaultStrokeThickness, 1d);

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
            RecordingFps = _recordingFps,
            RecordingJpegQuality = _recordingJpegQuality,
            GifFps = GifFps,
            RecordingCursorHighlightEnabled = RecordingCursorHighlightEnabled,
            RecordingClickRippleEnabled = RecordingClickRippleEnabled,
            RecordingCursorHighlightSize = clampedRecordingCursorHighlightSize,
            CaptureDelaySeconds = CaptureDelaySeconds,
            HudGapPixels = _hudGapPixels,
            DefaultAnnotationColor = $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}",
            DefaultStrokeThickness = DefaultStrokeThickness,
            RegionCaptureHotkey = RegionCaptureHotkey,
            AutoUpdateCheckInterval = AutoUpdateCheckInterval,
            LastAutoUpdateCheckUtc = _lastAutoUpdateCheckUtc,
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
    private void ResetCurrentSection()
    {
        var defaults = new UserSettings();
        switch (SelectedSection)
        {
            case SettingsSection.Capture:
                ScreenshotSavePath = defaults.ScreenshotSavePath;
                AutoSaveScreenshots = defaults.AutoSaveScreenshots;
                CaptureDelaySeconds = defaults.CaptureDelaySeconds;
                RegionCaptureHotkey = defaults.RegionCaptureHotkey;
                IsRecordingHotkey = false;
                break;
            case SettingsSection.Recording:
                RecordingOutputPath = defaults.RecordingOutputPath;
                RecordingFormat = defaults.RecordingFormat;
                GifFps = defaults.GifFps;
                RecordingCursorHighlightEnabled = defaults.RecordingCursorHighlightEnabled;
                RecordingClickRippleEnabled = defaults.RecordingClickRippleEnabled;
                RecordingCursorHighlightSize = ClampRecordingCursorHighlightSize(defaults.RecordingCursorHighlightSize);
                break;
            case SettingsSection.Annotation:
                DefaultAnnotationColor = ParseAnnotationColorOrFallback(defaults.DefaultAnnotationColor);
                DefaultStrokeThickness = defaults.DefaultStrokeThickness;
                break;
            case SettingsSection.App:
                AutoUpdateCheckInterval = defaults.AutoUpdateCheckInterval;
                AppTheme = defaults.Theme;
                break;
        }
    }

    [RelayCommand]
    private void RestoreDefaults()
    {
        var defaults = new UserSettings();
        _recordingFps = defaults.RecordingFps;
        _recordingJpegQuality = defaults.RecordingJpegQuality;
        _hudGapPixels = defaults.HudGapPixels;
        _lastAutoUpdateCheckUtc = defaults.LastAutoUpdateCheckUtc;
        ScreenshotSavePath = defaults.ScreenshotSavePath;
        AutoSaveScreenshots = defaults.AutoSaveScreenshots;
        RecordingOutputPath = defaults.RecordingOutputPath;
        RecordingFormat = defaults.RecordingFormat;
        GifFps = defaults.GifFps;
        RecordingCursorHighlightEnabled = defaults.RecordingCursorHighlightEnabled;
        RecordingClickRippleEnabled = defaults.RecordingClickRippleEnabled;
        RecordingCursorHighlightSize = ClampRecordingCursorHighlightSize(defaults.RecordingCursorHighlightSize);
        CaptureDelaySeconds = defaults.CaptureDelaySeconds;
        DefaultAnnotationColor = ParseAnnotationColorOrFallback(defaults.DefaultAnnotationColor);
        DefaultStrokeThickness = defaults.DefaultStrokeThickness;
        RegionCaptureHotkey = defaults.RegionCaptureHotkey;
        IsRecordingHotkey = false;
        AutoUpdateCheckInterval = defaults.AutoUpdateCheckInterval;
        AppTheme = defaults.Theme;
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

    private static Color ParseAnnotationColorOrFallback(string colorText)
    {
        try
        {
            return (Color)System.Windows.Media.ColorConverter.ConvertFromString(colorText);
        }
        catch
        {
            return Colors.Red;
        }
    }
}
