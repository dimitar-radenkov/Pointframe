using System.Collections.ObjectModel;
using System.Windows.Media;
using Pointframe.Services;

namespace Pointframe.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private const double MinRecordingCursorHighlightSize = 8d;
    private const double MaxRecordingCursorHighlightSize = 96d;
    private static readonly SettingsSectionItem[] SectionItems =
    [
        new(SettingsSection.Capture, "Capture", "Screenshot defaults."),
        new(SettingsSection.SmartRedaction, "Smart redaction", "Built-in patterns and custom rules."),
        new(SettingsSection.Recording, "Recording", "Recording output, pointer effects, and advanced options."),
        new(SettingsSection.Annotation, "Annotation", "Annotation defaults, presets, and watermark."),
        new(SettingsSection.Shortcuts, "Shortcuts", "All keyboard shortcuts in one place."),
        new(SettingsSection.App, "App", "Theme and update checks."),
    ];

    private sealed record OverlayShortcutDescriptor(
        string Key,
        string Label,
        Func<UserSettings, HotkeyBinding> SettingOf,
        Func<SettingsViewModel, HotkeyBinding> Get,
        Action<SettingsViewModel, HotkeyBinding> Set);

    private sealed record SmartRedactionBuiltInPatternDefinition(
        SensitiveDataType Type,
        string Name,
        string Description,
        string Example,
        string Pattern);

    private static readonly OverlayShortcutDescriptor[] OverlayShortcutDescriptors =
    [
        new("OverlayCopy", "Copy snip",
            s => new(s.OverlayCopyHotkey, s.OverlayCopyHotkeyModifiers),
            vm => new(vm.OverlayCopyHotkey, vm.OverlayCopyHotkeyModifiers),
            (vm, b) => (vm.OverlayCopyHotkey, vm.OverlayCopyHotkeyModifiers) = (b.Key, b.Modifiers)),
        new("OverlaySaveAs", "Save As",
            s => new(s.OverlaySaveAsHotkey, s.OverlaySaveAsHotkeyModifiers),
            vm => new(vm.OverlaySaveAsHotkey, vm.OverlaySaveAsHotkeyModifiers),
            (vm, b) => (vm.OverlaySaveAsHotkey, vm.OverlaySaveAsHotkeyModifiers) = (b.Key, b.Modifiers)),
        new("OverlayUndo", "Undo",
            s => new(s.OverlayUndoHotkey, s.OverlayUndoHotkeyModifiers),
            vm => new(vm.OverlayUndoHotkey, vm.OverlayUndoHotkeyModifiers),
            (vm, b) => (vm.OverlayUndoHotkey, vm.OverlayUndoHotkeyModifiers) = (b.Key, b.Modifiers)),
        new("OverlayRedo", "Redo",
            s => new(s.OverlayRedoHotkey, s.OverlayRedoHotkeyModifiers),
            vm => new(vm.OverlayRedoHotkey, vm.OverlayRedoHotkeyModifiers),
            (vm, b) => (vm.OverlayRedoHotkey, vm.OverlayRedoHotkeyModifiers) = (b.Key, b.Modifiers)),
        new("OverlayToggleShortcuts", "Show/hide overlay shortcuts",
            s => new(s.OverlayToggleShortcutsHotkey, s.OverlayToggleShortcutsHotkeyModifiers),
            vm => new(vm.OverlayToggleShortcutsHotkey, vm.OverlayToggleShortcutsHotkeyModifiers),
            (vm, b) => (vm.OverlayToggleShortcutsHotkey, vm.OverlayToggleShortcutsHotkeyModifiers) = (b.Key, b.Modifiers)),
        new("OverlayClose", "Close overlay",
            s => new(s.OverlayCloseHotkey, s.OverlayCloseHotkeyModifiers),
            vm => new(vm.OverlayCloseHotkey, vm.OverlayCloseHotkeyModifiers),
            (vm, b) => (vm.OverlayCloseHotkey, vm.OverlayCloseHotkeyModifiers) = (b.Key, b.Modifiers)),
    ];

    private static readonly SmartRedactionBuiltInPatternDefinition[] SmartRedactionBuiltInPatterns =
    [
        new(
            SensitiveDataType.Email,
            "Email addresses",
            "Detects email-like addresses.",
            "dev@example.com",
            @"\b[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}\b"),
        new(
            SensitiveDataType.Phone,
            "Phone numbers",
            "Detects phone numbers and tolerates common OCR digit substitutions.",
            "555-123-4567",
            @"\b(?:\+?\d[\d\-\s().]{6,}\d)\b"),
        new(
            SensitiveDataType.UrlQueryToken,
            "URL/query secrets",
            "Detects token-like values in query strings or key=value text.",
            "token=abc123def456",
            @"\b(?:token|access_token|apikey|api_key|secret|password)\s*=\s*[^&\s]+"),
        new(
            SensitiveDataType.Ipv4,
            "IPv4 addresses",
            "Detects IPv4 addresses and tolerates common OCR digit substitutions.",
            "192.168.10.42",
            @"\b(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(?:\.(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)){3}\b"),
        new(
            SensitiveDataType.AccessKeyLike,
            "Access key-like strings",
            "Detects common token formats such as AWS keys and GitHub personal access tokens.",
            "ghp_abcdefghijklmnopqrstuvwxyz123456",
            @"\b(?:AKIA[0-9A-Z]{16}|ghp_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{20,})\b"),
        new(
            SensitiveDataType.JwtLike,
            "JWT-like tokens",
            "Detects three-part JWT-style tokens.",
            "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.signaturepart",
            @"\b[A-Za-z0-9\-_]{12,}\.[A-Za-z0-9\-_]{12,}\.[A-Za-z0-9\-_]{12,}\b"),
    ];

    private static OverlayShortcutDescriptor? FindOverlayShortcut(string shortcutKey) =>
        Array.Find(OverlayShortcutDescriptors, descriptor => descriptor.Key == shortcutKey);

    private readonly IDialogService _dialogService;
    private readonly IMicrophoneDeviceService _microphoneDeviceService;
    private readonly IUserSettingsService _settingsService;
    private readonly ITelemetryService _telemetry;
    private readonly IThemeService _themeService;
    private readonly AppTheme _originalTheme;
    private readonly IReadOnlyList<string> _availableMicrophoneDevices;
    private int _recordingFps;
    private int _hudGapPixels;
    private DateTime? _lastAutoUpdateCheckUtc;
    private readonly ScreenshotWatermarkSettings _watermarkOther;

    public SettingsViewModel(
        IUserSettingsService settingsService,
        IThemeService themeService,
        IDialogService dialogService,
        IMicrophoneDeviceService microphoneDeviceService)
        : this(settingsService, themeService, dialogService, microphoneDeviceService, NullTelemetryService.Instance)
    {
    }

    public SettingsViewModel(
        IUserSettingsService settingsService,
        IThemeService themeService,
        IDialogService dialogService,
        IMicrophoneDeviceService microphoneDeviceService,
        ITelemetryService telemetry)
    {
        _dialogService = dialogService;
        _microphoneDeviceService = microphoneDeviceService;
        _settingsService = settingsService;
        _telemetry = telemetry;
        _themeService = themeService;
        _availableMicrophoneDevices = microphoneDeviceService.GetAvailableCaptureDeviceNames();

        var s = settingsService.Current;
        _screenshotSavePath = s.ScreenshotSavePath;
        _autoSaveScreenshots = s.AutoSaveScreenshots;
        _smartRedactionEnabled = s.SmartRedactionEnabled;
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
        _cleanWindowCaptureHotkey = s.CleanWindowCaptureHotkey;
        _cleanWindowCaptureHotkeyModifiers = s.CleanWindowCaptureHotkeyModifiers;
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

        var watermark = s.ScreenshotWatermark ?? new ScreenshotWatermarkSettings();
        _watermarkOther = watermark;
        _watermarkEnabled = watermark.Enabled;
        _watermarkTextTemplate = watermark.TextTemplate;
        _watermarkPosition = watermark.Position;
        _watermarkFontSize = watermark.FontSize;
        _watermarkApplyToCopy = watermark.ApplyToCopy;
        _watermarkApplyToSave = watermark.ApplyToSave;

        _defaultAnnotationColor = ParseAnnotationColorOrFallback(s.DefaultAnnotationColor);
        _stylePresets = new ObservableCollection<AnnotationStylePresetViewModel>(
            s.StylePresets.Select(p => new AnnotationStylePresetViewModel(p)));
        _stylePresets.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CanAddPreset));
            AddPresetCommand.NotifyCanExecuteChanged();
        };
        var excludedBuiltInPatternTypes = (s.SmartRedactionExcludedBuiltInTypes ?? [])
            .ToHashSet();
        _builtInSmartRedactionPatterns = new ObservableCollection<SmartRedactionBuiltInPatternViewModel>(
            SmartRedactionBuiltInPatterns.Select(pattern => new SmartRedactionBuiltInPatternViewModel(
                pattern.Type,
                pattern.Name,
                pattern.Description,
                pattern.Example,
                pattern.Pattern,
                !excludedBuiltInPatternTypes.Contains(pattern.Type))));
        _customRedactionPatterns = new ObservableCollection<SmartRedactionPatternViewModel>(
            (s.CustomRedactionPatterns ?? []).Select(pattern => new SmartRedactionPatternViewModel(pattern)));
        _customRedactionPatterns.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CanAddCustomRedactionPattern));
            AddCustomRedactionPatternCommand.NotifyCanExecuteChanged();
        };

        _telemetry.TrackEvent(TelemetryEvents.SettingsOpened, new Dictionary<string, string>
        {
            [TelemetryPropertyKeys.AppSection] = SelectedSection.ToString().ToLowerInvariant(),
        });
    }

    public IReadOnlyList<SettingsSectionItem> Sections => SectionItems;

    [ObservableProperty]
    private string _screenshotSavePath;

    [ObservableProperty]
    private bool _autoSaveScreenshots;

    [ObservableProperty]
    private bool _smartRedactionEnabled;

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
    private bool _watermarkEnabled;

    [ObservableProperty]
    private WatermarkTextTemplate _watermarkTextTemplate;

    public IReadOnlyList<WatermarkTextTemplate> WatermarkTextTemplates { get; } = Enum.GetValues<WatermarkTextTemplate>();

    [ObservableProperty]
    private WatermarkPosition _watermarkPosition;

    [ObservableProperty]
    private double _watermarkFontSize;

    [ObservableProperty]
    private bool _watermarkApplyToCopy;

    [ObservableProperty]
    private bool _watermarkApplyToSave;

    public IReadOnlyList<WatermarkPosition> WatermarkPositions { get; } = Enum.GetValues<WatermarkPosition>();

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
    [NotifyPropertyChangedFor(nameof(CleanWindowCaptureHotkeyDisplayName))]
    private uint _cleanWindowCaptureHotkey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CleanWindowCaptureHotkeyDisplayName))]
    private HotkeyModifiers _cleanWindowCaptureHotkeyModifiers;

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
    private bool _isCapturingCleanWindowCaptureHotkey;

    [ObservableProperty]
    private UpdateCheckInterval _autoUpdateCheckInterval;

    [ObservableProperty]
    private AppTheme _appTheme;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedSectionItem))]
    [NotifyPropertyChangedFor(nameof(SelectedSectionDisplayName))]
    [NotifyPropertyChangedFor(nameof(SelectedSectionDescription))]
    [NotifyPropertyChangedFor(nameof(IsCaptureSectionSelected))]
    [NotifyPropertyChangedFor(nameof(IsSmartRedactionSectionSelected))]
    [NotifyPropertyChangedFor(nameof(IsRecordingSectionSelected))]
    [NotifyPropertyChangedFor(nameof(IsAnnotationSectionSelected))]
    [NotifyPropertyChangedFor(nameof(IsShortcutsSectionSelected))]
    [NotifyPropertyChangedFor(nameof(IsAppSectionSelected))]
    private SettingsSection _selectedSection = SettingsSection.Capture;

    public SettingsSectionItem SelectedSectionItem =>
        Array.Find(SectionItems, item => item.Section == SelectedSection) ?? SectionItems[0];

    public string RegionCaptureHotkeyDisplayName => new HotkeyBinding(RegionCaptureHotkey, RegionCaptureHotkeyModifiers).DisplayName;
    public string WholeScreenRecordHotkeyDisplayName => new HotkeyBinding(WholeScreenRecordHotkey, WholeScreenRecordHotkeyModifiers).DisplayName;
    public string CleanWindowCaptureHotkeyDisplayName => new HotkeyBinding(CleanWindowCaptureHotkey, CleanWindowCaptureHotkeyModifiers).DisplayName;
    public string OverlayCopyHotkeyDisplayName => new HotkeyBinding(OverlayCopyHotkey, OverlayCopyHotkeyModifiers).DisplayName;
    public string OverlaySaveAsHotkeyDisplayName => new HotkeyBinding(OverlaySaveAsHotkey, OverlaySaveAsHotkeyModifiers).DisplayName;
    public string OverlayUndoHotkeyDisplayName => new HotkeyBinding(OverlayUndoHotkey, OverlayUndoHotkeyModifiers).DisplayName;
    public string OverlayRedoHotkeyDisplayName => new HotkeyBinding(OverlayRedoHotkey, OverlayRedoHotkeyModifiers).DisplayName;
    public string OverlayToggleShortcutsHotkeyDisplayName => new HotkeyBinding(OverlayToggleShortcutsHotkey, OverlayToggleShortcutsHotkeyModifiers).DisplayName;
    public string OverlayCloseHotkeyDisplayName => new HotkeyBinding(OverlayCloseHotkey, OverlayCloseHotkeyModifiers).DisplayName;
    public bool HasOverlayShortcutConflict => !string.IsNullOrWhiteSpace(OverlayShortcutConflictMessage);
    public string SelectedSectionDisplayName => SelectedSectionItem.DisplayName;
    public string SelectedSectionDescription => SelectedSectionItem.Description;
    public IReadOnlyList<string> AvailableMicrophoneDevices => _availableMicrophoneDevices;
    public bool HasAvailableMicrophoneDevices => _availableMicrophoneDevices.Count > 0;
    public bool IsCaptureSectionSelected => SelectedSection == SettingsSection.Capture;
    public bool IsSmartRedactionSectionSelected => SelectedSection == SettingsSection.SmartRedaction;
    public bool IsRecordingSectionSelected => SelectedSection == SettingsSection.Recording;
    public bool IsAnnotationSectionSelected => SelectedSection == SettingsSection.Annotation;
    public bool IsShortcutsSectionSelected => SelectedSection == SettingsSection.Shortcuts;
    public bool IsAppSectionSelected => SelectedSection == SettingsSection.App;

    partial void OnDefaultAnnotationColorChanged(Color value) =>
        OnPropertyChanged(nameof(ColorPreviewBrush));

    partial void OnAppThemeChanged(AppTheme value) => _themeService.Apply(value);

    partial void OnSelectedSectionChanged(SettingsSection value)
    {
        _telemetry.TrackEvent(TelemetryEvents.SettingsSectionChanged, new Dictionary<string, string>
        {
            [TelemetryPropertyKeys.AppSection] = value.ToString().ToLowerInvariant(),
        });
    }

    public SolidColorBrush ColorPreviewBrush => new(DefaultAnnotationColor);
    public double AnnotationPreviewThickness => Math.Max(DefaultStrokeThickness, 1d);

    private readonly ObservableCollection<AnnotationStylePresetViewModel> _stylePresets;
    public ObservableCollection<AnnotationStylePresetViewModel> StylePresets => _stylePresets;
    public bool CanAddPreset => _stylePresets.Count < AnnotationStylePreset.MaxCount;
    private readonly ObservableCollection<SmartRedactionBuiltInPatternViewModel> _builtInSmartRedactionPatterns;
    public ObservableCollection<SmartRedactionBuiltInPatternViewModel> BuiltInSmartRedactionPatterns => _builtInSmartRedactionPatterns;
    private readonly ObservableCollection<SmartRedactionPatternViewModel> _customRedactionPatterns;
    public ObservableCollection<SmartRedactionPatternViewModel> CustomRedactionPatterns => _customRedactionPatterns;
    public bool CanAddCustomRedactionPattern => _customRedactionPatterns.Count < SmartRedactionPattern.MaxCount;

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

    [RelayCommand(CanExecute = nameof(CanAddCustomRedactionPattern))]
    private void AddCustomRedactionPattern()
    {
        _customRedactionPatterns.Add(new SmartRedactionPatternViewModel(new SmartRedactionPattern
        {
            Name = $"Pattern {_customRedactionPatterns.Count + 1}",
            Pattern = string.Empty,
            IsEnabled = true,
        }));
    }

    [RelayCommand]
    private void RemoveCustomRedactionPattern(SmartRedactionPatternViewModel? pattern)
    {
        if (pattern is null)
        {
            return;
        }

        _customRedactionPatterns.Remove(pattern);
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
            SmartRedactionEnabled = SmartRedactionEnabled,
            SmartRedactionExcludedBuiltInTypes =
            [
                .. _builtInSmartRedactionPatterns
                    .Where(pattern => !pattern.IsEnabled)
                    .Select(pattern => pattern.Type),
            ],
            CustomRedactionPatterns = [.. _customRedactionPatterns.Select(pattern => pattern.ToModel())],
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
            ScreenshotWatermark = new ScreenshotWatermarkSettings
            {
                Enabled = WatermarkEnabled,
                TextTemplate = WatermarkTextTemplate,
                Position = WatermarkPosition,
                FontSize = WatermarkFontSize,
                ApplyToCopy = WatermarkApplyToCopy,
                ApplyToSave = WatermarkApplyToSave,
                ColorHex = _watermarkOther.ColorHex,
                BackgroundEnabled = _watermarkOther.BackgroundEnabled,
                Opacity = _watermarkOther.Opacity,
                Margin = _watermarkOther.Margin,
            },
            VideoWatermark = new VideoWatermarkSettings
            {
                Enabled = WatermarkEnabled,
                TextTemplate = WatermarkTextTemplate,
                Position = WatermarkPosition,
                FontSize = WatermarkFontSize,
                ApplyToCopy = WatermarkApplyToCopy,
                ApplyToSave = WatermarkApplyToSave,
                ColorHex = _watermarkOther.ColorHex,
                BackgroundEnabled = _watermarkOther.BackgroundEnabled,
                Opacity = _watermarkOther.Opacity,
                Margin = _watermarkOther.Margin,
            },
            DefaultAnnotationColor = $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}",
            DefaultStrokeThickness = DefaultStrokeThickness,
            StylePresets = [.. _stylePresets.Select(p => p.ToModel())],
            RegionCaptureHotkey = RegionCaptureHotkey,
            RegionCaptureHotkeyModifiers = RegionCaptureHotkeyModifiers,
            WholeScreenRecordHotkey = WholeScreenRecordHotkey,
            WholeScreenRecordHotkeyModifiers = WholeScreenRecordHotkeyModifiers,
            CleanWindowCaptureHotkey = CleanWindowCaptureHotkey,
            CleanWindowCaptureHotkeyModifiers = CleanWindowCaptureHotkeyModifiers,
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
        _telemetry.TrackEvent(TelemetryEvents.SettingsSaved, new Dictionary<string, string>
        {
            [TelemetryPropertyKeys.AppSection] = SelectedSection.ToString().ToLowerInvariant(),
        });
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void StartRecordingHotkey()
    {
        IsCapturingWholeScreenRecordHotkey = false;
        IsCapturingCleanWindowCaptureHotkey = false;
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
        IsCapturingCleanWindowCaptureHotkey = false;
        IsCapturingOverlayShortcut = false;
        OverlayShortcutCaptureTarget = string.Empty;
        OverlayShortcutCaptureDisplayName = string.Empty;
        OverlayShortcutConflictMessage = string.Empty;
        IsCapturingWholeScreenRecordHotkey = true;
    }

    [RelayCommand]
    private void StartCapturingCleanWindowCaptureHotkey()
    {
        IsRecordingHotkey = false;
        IsCapturingOverlayShortcut = false;
        OverlayShortcutCaptureTarget = string.Empty;
        OverlayShortcutCaptureDisplayName = string.Empty;
        OverlayShortcutConflictMessage = string.Empty;
        IsCapturingWholeScreenRecordHotkey = false;
        IsCapturingCleanWindowCaptureHotkey = true;
    }

    [RelayCommand]
    private void ResetRecordHotkey()
    {
        WholeScreenRecordHotkey = 0x52; // VK_R
        WholeScreenRecordHotkeyModifiers = HotkeyModifiers.Ctrl | HotkeyModifiers.Shift;
        IsCapturingWholeScreenRecordHotkey = false;
    }

    [RelayCommand]
    private void ResetCleanWindowCaptureHotkey()
    {
        CleanWindowCaptureHotkey = 0x57; // VK_W
        CleanWindowCaptureHotkeyModifiers = HotkeyModifiers.Ctrl | HotkeyModifiers.Shift;
        IsCapturingCleanWindowCaptureHotkey = false;
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
        IsCapturingCleanWindowCaptureHotkey = false;
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
        var descriptor = FindOverlayShortcut(shortcutKey);
        descriptor?.Set(this, descriptor.SettingOf(new UserSettings()));
    }

    internal void ApplyOverlayShortcutCapture(uint vk, HotkeyModifiers modifiers)
    {
        var binding = new HotkeyBinding(vk, modifiers);
        var owner = Array.Find(OverlayShortcutDescriptors, descriptor => descriptor.Get(this) == binding);
        if (owner is not null && owner.Key != OverlayShortcutCaptureTarget)
        {
            OverlayShortcutConflictMessage = $"{binding.DisplayName} is already assigned to {owner.Label}.";
            return;
        }

        OverlayShortcutConflictMessage = string.Empty;
        var target = FindOverlayShortcut(OverlayShortcutCaptureTarget);
        if (target is null)
        {
            return;
        }

        target.Set(this, binding);
        CancelCapturingOverlayShortcut();
    }

    [RelayCommand]
    private void ResetCurrentSection()
    {
        _telemetry.TrackEvent(TelemetryEvents.SettingsSectionReset, new Dictionary<string, string>
        {
            [TelemetryPropertyKeys.AppSection] = SelectedSection.ToString().ToLowerInvariant(),
        });

        var defaults = new UserSettings();
        switch (SelectedSection)
        {
            case SettingsSection.Capture:
                ScreenshotSavePath = defaults.ScreenshotSavePath;
                AutoSaveScreenshots = defaults.AutoSaveScreenshots;
                CaptureDelaySeconds = defaults.CaptureDelaySeconds;
                break;
            case SettingsSection.SmartRedaction:
                SmartRedactionEnabled = defaults.SmartRedactionEnabled;
                ResetBuiltInSmartRedactionPatterns(defaults.SmartRedactionExcludedBuiltInTypes);
                ResetCustomRedactionPatterns(defaults.CustomRedactionPatterns);
                break;
            case SettingsSection.Recording:
                RecordingOutputPath = defaults.RecordingOutputPath;
                RecordMicrophone = defaults.RecordMicrophone;
                SelectedMicrophoneDeviceName = ResolveInitialMicrophoneDeviceName(defaults.RecordingMicrophoneDeviceName);
                GifFps = defaults.GifFps;
                RecordingCursorHighlightEnabled = defaults.RecordingCursorHighlightEnabled;
                RecordingClickRippleEnabled = defaults.RecordingClickRippleEnabled;
                RecordingCursorHighlightSize = ClampRecordingCursorHighlightSize(defaults.RecordingCursorHighlightSize);
                break;
            case SettingsSection.Annotation:
                DefaultAnnotationColor = ParseAnnotationColorOrFallback(defaults.DefaultAnnotationColor);
                DefaultStrokeThickness = defaults.DefaultStrokeThickness;
                ResetStylePresets(defaults.StylePresets);
                WatermarkEnabled = defaults.ScreenshotWatermark.Enabled;
                WatermarkTextTemplate = defaults.ScreenshotWatermark.TextTemplate;
                WatermarkPosition = defaults.ScreenshotWatermark.Position;
                WatermarkFontSize = defaults.ScreenshotWatermark.FontSize;
                WatermarkApplyToCopy = defaults.ScreenshotWatermark.ApplyToCopy;
                WatermarkApplyToSave = defaults.ScreenshotWatermark.ApplyToSave;
                break;
            case SettingsSection.App:
                AutoUpdateCheckInterval = defaults.AutoUpdateCheckInterval;
                AppTheme = defaults.Theme;
                break;
            case SettingsSection.Shortcuts:
                RegionCaptureHotkey = defaults.RegionCaptureHotkey;
                RegionCaptureHotkeyModifiers = defaults.RegionCaptureHotkeyModifiers;
                IsRecordingHotkey = false;
                WholeScreenRecordHotkey = defaults.WholeScreenRecordHotkey;
                WholeScreenRecordHotkeyModifiers = defaults.WholeScreenRecordHotkeyModifiers;
                IsCapturingWholeScreenRecordHotkey = false;
                CleanWindowCaptureHotkey = defaults.CleanWindowCaptureHotkey;
                CleanWindowCaptureHotkeyModifiers = defaults.CleanWindowCaptureHotkeyModifiers;
                IsCapturingCleanWindowCaptureHotkey = false;
                ResetOverlayShortcutsTo(defaults);
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
        _telemetry.TrackEvent(TelemetryEvents.SettingsDefaultsRestored);

        var defaults = new UserSettings();
        _recordingFps = defaults.RecordingFps;
        _hudGapPixels = defaults.HudGapPixels;
        _lastAutoUpdateCheckUtc = defaults.LastAutoUpdateCheckUtc;
        ScreenshotSavePath = defaults.ScreenshotSavePath;
        AutoSaveScreenshots = defaults.AutoSaveScreenshots;
        SmartRedactionEnabled = defaults.SmartRedactionEnabled;
        ResetBuiltInSmartRedactionPatterns(defaults.SmartRedactionExcludedBuiltInTypes);
        ResetCustomRedactionPatterns(defaults.CustomRedactionPatterns);
        RecordingOutputPath = defaults.RecordingOutputPath;
        RecordMicrophone = defaults.RecordMicrophone;
        SelectedMicrophoneDeviceName = ResolveInitialMicrophoneDeviceName(defaults.RecordingMicrophoneDeviceName);
        GifFps = defaults.GifFps;
        RecordingCursorHighlightEnabled = defaults.RecordingCursorHighlightEnabled;
        RecordingClickRippleEnabled = defaults.RecordingClickRippleEnabled;
        RecordingCursorHighlightSize = ClampRecordingCursorHighlightSize(defaults.RecordingCursorHighlightSize);
        CaptureDelaySeconds = defaults.CaptureDelaySeconds;
        WatermarkEnabled = defaults.ScreenshotWatermark.Enabled;
        WatermarkTextTemplate = defaults.ScreenshotWatermark.TextTemplate;
        WatermarkPosition = defaults.ScreenshotWatermark.Position;
        WatermarkFontSize = defaults.ScreenshotWatermark.FontSize;
        WatermarkApplyToCopy = defaults.ScreenshotWatermark.ApplyToCopy;
        WatermarkApplyToSave = defaults.ScreenshotWatermark.ApplyToSave;
        DefaultAnnotationColor = ParseAnnotationColorOrFallback(defaults.DefaultAnnotationColor);
        DefaultStrokeThickness = defaults.DefaultStrokeThickness;
        ResetStylePresets(defaults.StylePresets);
        RegionCaptureHotkey = defaults.RegionCaptureHotkey;
        RegionCaptureHotkeyModifiers = defaults.RegionCaptureHotkeyModifiers;
        IsRecordingHotkey = false;
        WholeScreenRecordHotkey = defaults.WholeScreenRecordHotkey;
        WholeScreenRecordHotkeyModifiers = defaults.WholeScreenRecordHotkeyModifiers;
        IsCapturingWholeScreenRecordHotkey = false;
        CleanWindowCaptureHotkey = defaults.CleanWindowCaptureHotkey;
        CleanWindowCaptureHotkeyModifiers = defaults.CleanWindowCaptureHotkeyModifiers;
        IsCapturingCleanWindowCaptureHotkey = false;
        ResetOverlayShortcutsTo(defaults);
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
        _telemetry.TrackEvent(TelemetryEvents.SettingsCanceled);
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

    private void ResetBuiltInSmartRedactionPatterns(IReadOnlyList<SensitiveDataType>? excludedPatternTypes)
    {
        var excludedPatternTypeSet = (excludedPatternTypes ?? []).ToHashSet();
        foreach (var pattern in _builtInSmartRedactionPatterns)
        {
            pattern.IsEnabled = !excludedPatternTypeSet.Contains(pattern.Type);
        }
    }

    private void ResetCustomRedactionPatterns(List<SmartRedactionPattern> patterns)
    {
        _customRedactionPatterns.Clear();
        foreach (var pattern in patterns)
        {
            _customRedactionPatterns.Add(new SmartRedactionPatternViewModel(pattern));
        }

        AddCustomRedactionPatternCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanAddCustomRedactionPattern));
    }

    private void ResetOverlayShortcutsTo(UserSettings settings)
    {
        foreach (var descriptor in OverlayShortcutDescriptors)
        {
            descriptor.Set(this, descriptor.SettingOf(settings));
        }
    }

    private static string OverlayShortcutLabel(string shortcutKey) =>
        FindOverlayShortcut(shortcutKey)?.Label ?? "Shortcut";

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

