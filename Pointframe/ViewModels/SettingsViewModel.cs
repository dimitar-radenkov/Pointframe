using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pointframe.Services;

namespace Pointframe.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private const double MinRecordingCursorHighlightSize = 8d;
    private const double MaxRecordingCursorHighlightSize = 96d;
    private static readonly SettingsSectionItem[] SectionItems =
    [
        new(SettingsSection.Capture, "Capture", "Screenshot folders, timing, and the capture shortcut."),
        new(SettingsSection.Recording, "Recording", "Output options, cursor effects, and advanced recording defaults."),
        new(SettingsSection.Annotation, "Annotation", "Default annotation appearance and preview."),
        new(SettingsSection.Shortcuts, "Shortcuts", "See all capture, recording, and overlay keyboard shortcuts."),
        new(SettingsSection.App, "App", "Appearance, update checks, and reset actions."),
    ];

    private readonly IDialogService _dialogService;
    private readonly IMicrophoneDeviceService _microphoneDeviceService;
    private readonly IUserSettingsService _settingsService;
    private readonly IThemeService _themeService;
    private readonly AppTheme _originalTheme;
    private readonly IReadOnlyList<string> _availableMicrophoneDevices;
    private int _recordingFps;
    private int _hudGapPixels;
    private DateTime? _lastAutoUpdateCheckUtc;

    public SettingsViewModel(IUserSettingsService settingsService, IThemeService themeService, IDialogService dialogService, IMicrophoneDeviceService microphoneDeviceService)
    {
        _dialogService = dialogService;
        _microphoneDeviceService = microphoneDeviceService;
        _settingsService = settingsService;
        _themeService = themeService;
        _availableMicrophoneDevices = microphoneDeviceService.GetAvailableCaptureDeviceNames();

        var s = settingsService.Current;
        _screenshotSavePath = s.ScreenshotSavePath;
        _autoSaveScreenshots = s.AutoSaveScreenshots;
        _recordingOutputPath = s.RecordingOutputPath;
        _recordMicrophone = s.RecordMicrophone;
        _selectedMicrophoneDeviceName = ResolveInitialMicrophoneDeviceName(s.RecordingMicrophoneDeviceName);
        _gifFps = s.GifFps;
        _recordingCursorHighlightEnabled = s.RecordingCursorHighlightEnabled;
        _recordingClickRippleEnabled = s.RecordingClickRippleEnabled;
        _recordingCursorHighlightSize = ClampRecordingCursorHighlightSize(s.RecordingCursorHighlightSize);
        _captureDelaySeconds = s.CaptureDelaySeconds;
        _defaultStrokeThickness = s.DefaultStrokeThickness;
        _regionCaptureHotkey = s.RegionCaptureHotkey;
        _regionCaptureHotkeyModifiers = s.RegionCaptureHotkeyModifiers;
        _wholeScreenRecordHotkey = s.WholeScreenRecordHotkey;
        _wholeScreenRecordHotkeyModifiers = s.WholeScreenRecordHotkeyModifiers;
        _overlayCopyHotkey = s.OverlayCopyHotkey;
        _overlayCopyHotkeyModifiers = s.OverlayCopyHotkeyModifiers;
        _overlaySaveAsHotkey = s.OverlaySaveAsHotkey;
        _overlaySaveAsHotkeyModifiers = s.OverlaySaveAsHotkeyModifiers;
        _overlayUndoHotkey = s.OverlayUndoHotkey;
        _overlayUndoHotkeyModifiers = s.OverlayUndoHotkeyModifiers;
        _overlayRedoHotkey = s.OverlayRedoHotkey;
        _overlayRedoHotkeyModifiers = s.OverlayRedoHotkeyModifiers;
        _overlayToggleShortcutsHotkey = s.OverlayToggleShortcutsHotkey;
        _overlayToggleShortcutsHotkeyModifiers = s.OverlayToggleShortcutsHotkeyModifiers;
        _overlayCloseHotkey = s.OverlayCloseHotkey;
        _overlayCloseHotkeyModifiers = s.OverlayCloseHotkeyModifiers;
        _autoUpdateCheckInterval = s.AutoUpdateCheckInterval;
        _appTheme = s.Theme;
        _originalTheme = s.Theme;
        _recordingFps = s.RecordingFps;
        _hudGapPixels = s.HudGapPixels;
        _lastAutoUpdateCheckUtc = s.LastAutoUpdateCheckUtc;

        _defaultAnnotationColor = ParseAnnotationColorOrFallback(s.DefaultAnnotationColor);
        _stylePresets = new ObservableCollection<AnnotationStylePresetViewModel>(
            s.StylePresets.Select(p => new AnnotationStylePresetViewModel(p)));
        _stylePresets.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CanAddPreset));
            AddPresetCommand.NotifyCanExecuteChanged();
        };
    }

    public IReadOnlyList<SettingsSectionItem> Sections => SectionItems;

    [ObservableProperty]
    private string _screenshotSavePath;

    [ObservableProperty]
    private bool _autoSaveScreenshots;

    [ObservableProperty]
    private string _recordingOutputPath;

    [ObservableProperty]
    private bool _recordMicrophone;

    [ObservableProperty]
    private string? _selectedMicrophoneDeviceName;

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
    [NotifyPropertyChangedFor(nameof(RegionCaptureHotkeyDisplayName))]
    private HotkeyModifiers _regionCaptureHotkeyModifiers;

    [ObservableProperty]
    private bool _isRecordingHotkey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WholeScreenRecordHotkeyDisplayName))]
    private uint _wholeScreenRecordHotkey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WholeScreenRecordHotkeyDisplayName))]
    private HotkeyModifiers _wholeScreenRecordHotkeyModifiers;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverlayCopyHotkeyDisplayName))]
    private uint _overlayCopyHotkey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverlayCopyHotkeyDisplayName))]
    private HotkeyModifiers _overlayCopyHotkeyModifiers;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverlaySaveAsHotkeyDisplayName))]
    private uint _overlaySaveAsHotkey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverlaySaveAsHotkeyDisplayName))]
    private HotkeyModifiers _overlaySaveAsHotkeyModifiers;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverlayUndoHotkeyDisplayName))]
    private uint _overlayUndoHotkey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverlayUndoHotkeyDisplayName))]
    private HotkeyModifiers _overlayUndoHotkeyModifiers;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverlayRedoHotkeyDisplayName))]
    private uint _overlayRedoHotkey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverlayRedoHotkeyDisplayName))]
    private HotkeyModifiers _overlayRedoHotkeyModifiers;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverlayToggleShortcutsHotkeyDisplayName))]
    private uint _overlayToggleShortcutsHotkey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverlayToggleShortcutsHotkeyDisplayName))]
    private HotkeyModifiers _overlayToggleShortcutsHotkeyModifiers;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverlayCloseHotkeyDisplayName))]
    private uint _overlayCloseHotkey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverlayCloseHotkeyDisplayName))]
    private HotkeyModifiers _overlayCloseHotkeyModifiers;

    [ObservableProperty]
    private bool _isCapturingOverlayShortcut;

    [ObservableProperty]
    private string _overlayShortcutCaptureTarget = string.Empty;

    [ObservableProperty]
    private string _overlayShortcutCaptureDisplayName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOverlayShortcutConflict))]
    private string _overlayShortcutConflictMessage = string.Empty;

    [ObservableProperty]
    private bool _isCapturingWholeScreenRecordHotkey;

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
    [NotifyPropertyChangedFor(nameof(IsShortcutsSectionSelected))]
    [NotifyPropertyChangedFor(nameof(IsAppSectionSelected))]
    private SettingsSection _selectedSection = SettingsSection.Capture;

    public SettingsSectionItem SelectedSectionItem =>
        Array.Find(SectionItems, item => item.Section == SelectedSection) ?? SectionItems[0];

    public string RegionCaptureHotkeyDisplayName => BuildHotkeyDisplayName(RegionCaptureHotkey, RegionCaptureHotkeyModifiers);
    public string WholeScreenRecordHotkeyDisplayName => BuildHotkeyDisplayName(WholeScreenRecordHotkey, WholeScreenRecordHotkeyModifiers);
    public string OverlayCopyHotkeyDisplayName => BuildHotkeyDisplayName(OverlayCopyHotkey, OverlayCopyHotkeyModifiers);
    public string OverlaySaveAsHotkeyDisplayName => BuildHotkeyDisplayName(OverlaySaveAsHotkey, OverlaySaveAsHotkeyModifiers);
    public string OverlayUndoHotkeyDisplayName => BuildHotkeyDisplayName(OverlayUndoHotkey, OverlayUndoHotkeyModifiers);
    public string OverlayRedoHotkeyDisplayName => BuildHotkeyDisplayName(OverlayRedoHotkey, OverlayRedoHotkeyModifiers);
    public string OverlayToggleShortcutsHotkeyDisplayName => BuildHotkeyDisplayName(OverlayToggleShortcutsHotkey, OverlayToggleShortcutsHotkeyModifiers);
    public string OverlayCloseHotkeyDisplayName => BuildHotkeyDisplayName(OverlayCloseHotkey, OverlayCloseHotkeyModifiers);
    public bool HasOverlayShortcutConflict => !string.IsNullOrWhiteSpace(OverlayShortcutConflictMessage);
    public string SelectedSectionDisplayName => SelectedSectionItem.DisplayName;
    public string SelectedSectionDescription => SelectedSectionItem.Description;
    public IReadOnlyList<string> AvailableMicrophoneDevices => _availableMicrophoneDevices;
    public bool HasAvailableMicrophoneDevices => _availableMicrophoneDevices.Count > 0;
    public bool IsCaptureSectionSelected => SelectedSection == SettingsSection.Capture;
    public bool IsRecordingSectionSelected => SelectedSection == SettingsSection.Recording;
    public bool IsAnnotationSectionSelected => SelectedSection == SettingsSection.Annotation;
    public bool IsShortcutsSectionSelected => SelectedSection == SettingsSection.Shortcuts;
    public bool IsAppSectionSelected => SelectedSection == SettingsSection.App;

    partial void OnDefaultAnnotationColorChanged(Color value) =>
        OnPropertyChanged(nameof(ColorPreviewBrush));

    partial void OnAppThemeChanged(AppTheme value) => _themeService.Apply(value);

    public SolidColorBrush ColorPreviewBrush => new(DefaultAnnotationColor);
    public double AnnotationPreviewThickness => Math.Max(DefaultStrokeThickness, 1d);

    private readonly ObservableCollection<AnnotationStylePresetViewModel> _stylePresets;
    public ObservableCollection<AnnotationStylePresetViewModel> StylePresets => _stylePresets;
    public bool CanAddPreset => _stylePresets.Count < AnnotationStylePreset.MaxCount;

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

    [RelayCommand(CanExecute = nameof(CanAddPreset))]
    private void AddPreset()
    {
        _stylePresets.Add(new AnnotationStylePresetViewModel(new AnnotationStylePreset
        {
            Name = $"Preset {_stylePresets.Count + 1}",
            Color = $"#{DefaultAnnotationColor.A:X2}{DefaultAnnotationColor.R:X2}{DefaultAnnotationColor.G:X2}{DefaultAnnotationColor.B:X2}",
            StrokeThickness = DefaultStrokeThickness,
        }));
    }

    [RelayCommand]
    private void RemovePreset(AnnotationStylePresetViewModel preset)
    {
        _stylePresets.Remove(preset);
    }

    [RelayCommand]
    private void PickPresetColor(AnnotationStylePresetViewModel preset)
    {
        var selectedColor = _dialogService.PickColor(preset.Color);
        if (selectedColor.HasValue)
        {
            preset.Color = selectedColor.Value;
        }
    }

    [RelayCommand]
    private void Save()
    {
        var c = DefaultAnnotationColor;
        var clampedRecordingCursorHighlightSize = ClampRecordingCursorHighlightSize(RecordingCursorHighlightSize);
        var currentSettings = _settingsService.Current;
        RecordingCursorHighlightSize = clampedRecordingCursorHighlightSize;

        _settingsService.Save(new UserSettings
        {
            ScreenshotSavePath = ScreenshotSavePath,
            AutoSaveScreenshots = AutoSaveScreenshots,
            RecordingOutputPath = RecordingOutputPath,
            RecordMicrophone = RecordMicrophone,
            RecordingMicrophoneDeviceName = SelectedMicrophoneDeviceName,
            RecordingFps = _recordingFps,
            GifFps = GifFps,
            RecordingCursorHighlightEnabled = RecordingCursorHighlightEnabled,
            RecordingClickRippleEnabled = RecordingClickRippleEnabled,
            RecordingCursorHighlightSize = clampedRecordingCursorHighlightSize,
            CaptureDelaySeconds = CaptureDelaySeconds,
            HudGapPixels = _hudGapPixels,
            DefaultAnnotationColor = $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}",
            DefaultStrokeThickness = DefaultStrokeThickness,
            StylePresets = [.. _stylePresets.Select(p => p.ToModel())],
            RegionCaptureHotkey = RegionCaptureHotkey,
            RegionCaptureHotkeyModifiers = RegionCaptureHotkeyModifiers,
            WholeScreenRecordHotkey = WholeScreenRecordHotkey,
            WholeScreenRecordHotkeyModifiers = WholeScreenRecordHotkeyModifiers,
            OverlayCopyHotkey = OverlayCopyHotkey,
            OverlayCopyHotkeyModifiers = OverlayCopyHotkeyModifiers,
            OverlaySaveAsHotkey = OverlaySaveAsHotkey,
            OverlaySaveAsHotkeyModifiers = OverlaySaveAsHotkeyModifiers,
            OverlayUndoHotkey = OverlayUndoHotkey,
            OverlayUndoHotkeyModifiers = OverlayUndoHotkeyModifiers,
            OverlayRedoHotkey = OverlayRedoHotkey,
            OverlayRedoHotkeyModifiers = OverlayRedoHotkeyModifiers,
            OverlayToggleShortcutsHotkey = OverlayToggleShortcutsHotkey,
            OverlayToggleShortcutsHotkeyModifiers = OverlayToggleShortcutsHotkeyModifiers,
            OverlayCloseHotkey = OverlayCloseHotkey,
            OverlayCloseHotkeyModifiers = OverlayCloseHotkeyModifiers,
            AutoUpdateCheckInterval = AutoUpdateCheckInterval,
            LastAutoUpdateCheckUtc = _lastAutoUpdateCheckUtc,
            Theme = AppTheme,
            InstallId = currentSettings.InstallId,
            InstallCreatedUtc = currentSettings.InstallCreatedUtc,
            FirstCaptureCompletedTracked = currentSettings.FirstCaptureCompletedTracked,
            FirstRecordingCompletedTracked = currentSettings.FirstRecordingCompletedTracked,
        });
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void StartRecordingHotkey()
    {
        IsCapturingWholeScreenRecordHotkey = false;
        IsCapturingOverlayShortcut = false;
        OverlayShortcutCaptureTarget = string.Empty;
        OverlayShortcutCaptureDisplayName = string.Empty;
        OverlayShortcutConflictMessage = string.Empty;
        IsRecordingHotkey = true;
    }

    [RelayCommand]
    private void ResetHotkey()
    {
        RegionCaptureHotkey = 0x2C; // VK_SNAPSHOT (Print Screen)
        RegionCaptureHotkeyModifiers = HotkeyModifiers.None;
        IsRecordingHotkey = false;
    }

    [RelayCommand]
    private void StartCapturingWholeScreenRecordHotkey()
    {
        IsRecordingHotkey = false;
        IsCapturingOverlayShortcut = false;
        OverlayShortcutCaptureTarget = string.Empty;
        OverlayShortcutCaptureDisplayName = string.Empty;
        OverlayShortcutConflictMessage = string.Empty;
        IsCapturingWholeScreenRecordHotkey = true;
    }

    [RelayCommand]
    private void ResetRecordHotkey()
    {
        WholeScreenRecordHotkey = 0x52; // VK_R
        WholeScreenRecordHotkeyModifiers = HotkeyModifiers.Ctrl | HotkeyModifiers.Shift;
        IsCapturingWholeScreenRecordHotkey = false;
    }

    [RelayCommand]
    private void StartCapturingOverlayShortcut(string shortcutKey)
    {
        if (string.IsNullOrWhiteSpace(shortcutKey))
        {
            return;
        }

        IsRecordingHotkey = false;
        IsCapturingWholeScreenRecordHotkey = false;
        OverlayShortcutConflictMessage = string.Empty;
        OverlayShortcutCaptureTarget = shortcutKey;
        OverlayShortcutCaptureDisplayName = OverlayShortcutLabel(shortcutKey);
        IsCapturingOverlayShortcut = true;
    }

    [RelayCommand]
    private void CancelCapturingOverlayShortcut()
    {
        IsCapturingOverlayShortcut = false;
        OverlayShortcutCaptureTarget = string.Empty;
        OverlayShortcutCaptureDisplayName = string.Empty;
        OverlayShortcutConflictMessage = string.Empty;
    }

    [RelayCommand]
    private void ResetOverlayShortcut(string shortcutKey)
    {
        OverlayShortcutConflictMessage = string.Empty;
        var defaults = new UserSettings();
        switch (shortcutKey)
        {
            case "OverlayCopy":
                OverlayCopyHotkey = defaults.OverlayCopyHotkey;
                OverlayCopyHotkeyModifiers = defaults.OverlayCopyHotkeyModifiers;
                break;
            case "OverlaySaveAs":
                OverlaySaveAsHotkey = defaults.OverlaySaveAsHotkey;
                OverlaySaveAsHotkeyModifiers = defaults.OverlaySaveAsHotkeyModifiers;
                break;
            case "OverlayUndo":
                OverlayUndoHotkey = defaults.OverlayUndoHotkey;
                OverlayUndoHotkeyModifiers = defaults.OverlayUndoHotkeyModifiers;
                break;
            case "OverlayRedo":
                OverlayRedoHotkey = defaults.OverlayRedoHotkey;
                OverlayRedoHotkeyModifiers = defaults.OverlayRedoHotkeyModifiers;
                break;
            case "OverlayToggleShortcuts":
                OverlayToggleShortcutsHotkey = defaults.OverlayToggleShortcutsHotkey;
                OverlayToggleShortcutsHotkeyModifiers = defaults.OverlayToggleShortcutsHotkeyModifiers;
                break;
            case "OverlayClose":
                OverlayCloseHotkey = defaults.OverlayCloseHotkey;
                OverlayCloseHotkeyModifiers = defaults.OverlayCloseHotkeyModifiers;
                break;
        }
    }

    internal void ApplyOverlayShortcutCapture(uint vk, HotkeyModifiers modifiers)
    {
        if (TryFindOverlayShortcutOwner(vk, modifiers, out var owner) && owner != OverlayShortcutCaptureTarget)
        {
            OverlayShortcutConflictMessage = $"{BuildHotkeyDisplayName(vk, modifiers)} is already assigned to {OverlayShortcutLabel(owner)}.";
            return;
        }

        OverlayShortcutConflictMessage = string.Empty;
        switch (OverlayShortcutCaptureTarget)
        {
            case "OverlayCopy":
                OverlayCopyHotkey = vk;
                OverlayCopyHotkeyModifiers = modifiers;
                break;
            case "OverlaySaveAs":
                OverlaySaveAsHotkey = vk;
                OverlaySaveAsHotkeyModifiers = modifiers;
                break;
            case "OverlayUndo":
                OverlayUndoHotkey = vk;
                OverlayUndoHotkeyModifiers = modifiers;
                break;
            case "OverlayRedo":
                OverlayRedoHotkey = vk;
                OverlayRedoHotkeyModifiers = modifiers;
                break;
            case "OverlayToggleShortcuts":
                OverlayToggleShortcutsHotkey = vk;
                OverlayToggleShortcutsHotkeyModifiers = modifiers;
                break;
            case "OverlayClose":
                OverlayCloseHotkey = vk;
                OverlayCloseHotkeyModifiers = modifiers;
                break;
            default:
                return;
        }

        CancelCapturingOverlayShortcut();
    }

    private bool TryFindOverlayShortcutOwner(uint vk, HotkeyModifiers modifiers, out string owner)
    {
        if (OverlayCopyHotkey == vk && OverlayCopyHotkeyModifiers == modifiers)
        {
            owner = "OverlayCopy";
            return true;
        }

        if (OverlaySaveAsHotkey == vk && OverlaySaveAsHotkeyModifiers == modifiers)
        {
            owner = "OverlaySaveAs";
            return true;
        }

        if (OverlayUndoHotkey == vk && OverlayUndoHotkeyModifiers == modifiers)
        {
            owner = "OverlayUndo";
            return true;
        }

        if (OverlayRedoHotkey == vk && OverlayRedoHotkeyModifiers == modifiers)
        {
            owner = "OverlayRedo";
            return true;
        }

        if (OverlayToggleShortcutsHotkey == vk && OverlayToggleShortcutsHotkeyModifiers == modifiers)
        {
            owner = "OverlayToggleShortcuts";
            return true;
        }

        if (OverlayCloseHotkey == vk && OverlayCloseHotkeyModifiers == modifiers)
        {
            owner = "OverlayClose";
            return true;
        }

        owner = string.Empty;
        return false;
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
                RegionCaptureHotkeyModifiers = defaults.RegionCaptureHotkeyModifiers;
                IsRecordingHotkey = false;
                break;
            case SettingsSection.Recording:
                RecordingOutputPath = defaults.RecordingOutputPath;
                RecordMicrophone = defaults.RecordMicrophone;
                SelectedMicrophoneDeviceName = ResolveInitialMicrophoneDeviceName(defaults.RecordingMicrophoneDeviceName);
                GifFps = defaults.GifFps;
                RecordingCursorHighlightEnabled = defaults.RecordingCursorHighlightEnabled;
                RecordingClickRippleEnabled = defaults.RecordingClickRippleEnabled;
                RecordingCursorHighlightSize = ClampRecordingCursorHighlightSize(defaults.RecordingCursorHighlightSize);
                WholeScreenRecordHotkey = defaults.WholeScreenRecordHotkey;
                WholeScreenRecordHotkeyModifiers = defaults.WholeScreenRecordHotkeyModifiers;
                IsCapturingWholeScreenRecordHotkey = false;
                break;
            case SettingsSection.Annotation:
                DefaultAnnotationColor = ParseAnnotationColorOrFallback(defaults.DefaultAnnotationColor);
                DefaultStrokeThickness = defaults.DefaultStrokeThickness;
                ResetStylePresets(defaults.StylePresets);
                break;
            case SettingsSection.App:
                AutoUpdateCheckInterval = defaults.AutoUpdateCheckInterval;
                AppTheme = defaults.Theme;
                break;
            case SettingsSection.Shortcuts:
                OverlayCopyHotkey = defaults.OverlayCopyHotkey;
                OverlayCopyHotkeyModifiers = defaults.OverlayCopyHotkeyModifiers;
                OverlaySaveAsHotkey = defaults.OverlaySaveAsHotkey;
                OverlaySaveAsHotkeyModifiers = defaults.OverlaySaveAsHotkeyModifiers;
                OverlayUndoHotkey = defaults.OverlayUndoHotkey;
                OverlayUndoHotkeyModifiers = defaults.OverlayUndoHotkeyModifiers;
                OverlayRedoHotkey = defaults.OverlayRedoHotkey;
                OverlayRedoHotkeyModifiers = defaults.OverlayRedoHotkeyModifiers;
                OverlayToggleShortcutsHotkey = defaults.OverlayToggleShortcutsHotkey;
                OverlayToggleShortcutsHotkeyModifiers = defaults.OverlayToggleShortcutsHotkeyModifiers;
                OverlayCloseHotkey = defaults.OverlayCloseHotkey;
                OverlayCloseHotkeyModifiers = defaults.OverlayCloseHotkeyModifiers;
                IsCapturingOverlayShortcut = false;
                OverlayShortcutCaptureTarget = string.Empty;
                OverlayShortcutCaptureDisplayName = string.Empty;
                OverlayShortcutConflictMessage = string.Empty;
                break;
        }
    }

    [RelayCommand]
    private void RestoreDefaults()
    {
        var defaults = new UserSettings();
        _recordingFps = defaults.RecordingFps;
        _hudGapPixels = defaults.HudGapPixels;
        _lastAutoUpdateCheckUtc = defaults.LastAutoUpdateCheckUtc;
        ScreenshotSavePath = defaults.ScreenshotSavePath;
        AutoSaveScreenshots = defaults.AutoSaveScreenshots;
        RecordingOutputPath = defaults.RecordingOutputPath;
        RecordMicrophone = defaults.RecordMicrophone;
        SelectedMicrophoneDeviceName = ResolveInitialMicrophoneDeviceName(defaults.RecordingMicrophoneDeviceName);
        GifFps = defaults.GifFps;
        RecordingCursorHighlightEnabled = defaults.RecordingCursorHighlightEnabled;
        RecordingClickRippleEnabled = defaults.RecordingClickRippleEnabled;
        RecordingCursorHighlightSize = ClampRecordingCursorHighlightSize(defaults.RecordingCursorHighlightSize);
        CaptureDelaySeconds = defaults.CaptureDelaySeconds;
        DefaultAnnotationColor = ParseAnnotationColorOrFallback(defaults.DefaultAnnotationColor);
        DefaultStrokeThickness = defaults.DefaultStrokeThickness;
        ResetStylePresets(defaults.StylePresets);
        RegionCaptureHotkey = defaults.RegionCaptureHotkey;
        RegionCaptureHotkeyModifiers = defaults.RegionCaptureHotkeyModifiers;
        IsRecordingHotkey = false;
        WholeScreenRecordHotkey = defaults.WholeScreenRecordHotkey;
        WholeScreenRecordHotkeyModifiers = defaults.WholeScreenRecordHotkeyModifiers;
        IsCapturingWholeScreenRecordHotkey = false;
        OverlayCopyHotkey = defaults.OverlayCopyHotkey;
        OverlayCopyHotkeyModifiers = defaults.OverlayCopyHotkeyModifiers;
        OverlaySaveAsHotkey = defaults.OverlaySaveAsHotkey;
        OverlaySaveAsHotkeyModifiers = defaults.OverlaySaveAsHotkeyModifiers;
        OverlayUndoHotkey = defaults.OverlayUndoHotkey;
        OverlayUndoHotkeyModifiers = defaults.OverlayUndoHotkeyModifiers;
        OverlayRedoHotkey = defaults.OverlayRedoHotkey;
        OverlayRedoHotkeyModifiers = defaults.OverlayRedoHotkeyModifiers;
        OverlayToggleShortcutsHotkey = defaults.OverlayToggleShortcutsHotkey;
        OverlayToggleShortcutsHotkeyModifiers = defaults.OverlayToggleShortcutsHotkeyModifiers;
        OverlayCloseHotkey = defaults.OverlayCloseHotkey;
        OverlayCloseHotkeyModifiers = defaults.OverlayCloseHotkeyModifiers;
        IsCapturingOverlayShortcut = false;
        OverlayShortcutCaptureTarget = string.Empty;
        OverlayShortcutCaptureDisplayName = string.Empty;
        OverlayShortcutConflictMessage = string.Empty;
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

    private void ResetStylePresets(List<Models.AnnotationStylePreset> presets)
    {
        _stylePresets.Clear();
        foreach (var preset in presets)
        {
            _stylePresets.Add(new AnnotationStylePresetViewModel(preset));
        }

        AddPresetCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanAddPreset));
    }

    private static string VkToKeyName(uint vk) =>
        vk == 0x2C ? "Print Screen" : KeyInterop.KeyFromVirtualKey((int)vk).ToString();

    private static string OverlayShortcutLabel(string shortcutKey)
    {
        return shortcutKey switch
        {
            "OverlayCopy" => "Copy snip",
            "OverlaySaveAs" => "Save As",
            "OverlayUndo" => "Undo",
            "OverlayRedo" => "Redo",
            "OverlayToggleShortcuts" => "Show/hide overlay shortcuts",
            "OverlayClose" => "Close overlay",
            _ => "Shortcut",
        };
    }

    private static string BuildHotkeyDisplayName(uint vk, HotkeyModifiers modifiers)
    {
        if (vk == 0)
        {
            return "Not set";
        }

        var parts = new List<string>();
        if (modifiers.HasFlag(HotkeyModifiers.Ctrl))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        parts.Add(VkToKeyName(vk));
        return string.Join("+", parts);
    }

    private static double ClampRecordingCursorHighlightSize(double size)
    {
        return Math.Clamp(size, MinRecordingCursorHighlightSize, MaxRecordingCursorHighlightSize);
    }

    private string? ResolveInitialMicrophoneDeviceName(string? configuredDeviceName)
    {
        if (_availableMicrophoneDevices.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(configuredDeviceName))
        {
            var matchingConfiguredDevice = _availableMicrophoneDevices.FirstOrDefault(device =>
                string.Equals(device, configuredDeviceName, StringComparison.OrdinalIgnoreCase));
            if (matchingConfiguredDevice is not null)
            {
                return matchingConfiguredDevice;
            }
        }

        var defaultDeviceName = _microphoneDeviceService.GetDefaultCaptureDeviceName();
        if (!string.IsNullOrWhiteSpace(defaultDeviceName))
        {
            var matchingDefaultDevice = _availableMicrophoneDevices.FirstOrDefault(device =>
                string.Equals(device, defaultDeviceName, StringComparison.OrdinalIgnoreCase));
            if (matchingDefaultDevice is not null)
            {
                return matchingDefaultDevice;
            }
        }

        return _availableMicrophoneDevices[0];
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
