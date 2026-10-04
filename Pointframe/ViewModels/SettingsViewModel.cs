using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Windows.Media;
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
        new(SettingsSection.Sharing, "Sharing", "Upload captures to your configured destination."),
        new(SettingsSection.Shortcuts, "Shortcuts", "See all capture, recording, and overlay keyboard shortcuts."),
        new(SettingsSection.App, "App", "Appearance, update checks, and reset actions."),
    ];

    private sealed record OverlayShortcutDescriptor(
        string Key,
        string Label,
        Func<UserSettings, HotkeyBinding> SettingOf,
        Func<SettingsViewModel, HotkeyBinding> Get,
        Action<SettingsViewModel, HotkeyBinding> Set);

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

    private static OverlayShortcutDescriptor? FindOverlayShortcut(string shortcutKey) =>
        Array.Find(OverlayShortcutDescriptors, descriptor => descriptor.Key == shortcutKey);

    private readonly IDialogService _dialogService;
    private readonly IMicrophoneDeviceService _microphoneDeviceService;
    private readonly ITranscriptModelService _transcriptModelService;
    private readonly IUserSettingsService _settingsService;
    private readonly ITelemetryService _telemetry;
    private readonly IThemeService _themeService;
    private readonly AppTheme _originalTheme;
    private readonly IReadOnlyList<string> _availableMicrophoneDevices;
    private int _recordingFps;
    private int _hudGapPixels;
    private DateTime? _lastAutoUpdateCheckUtc;
    private WatermarkSettings _watermarkHiddenStyle;

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
        : this(settingsService, themeService, dialogService, microphoneDeviceService, telemetry, NullTranscriptModelService.Instance)
    {
    }

    public SettingsViewModel(
        IUserSettingsService settingsService,
        IThemeService themeService,
        IDialogService dialogService,
        IMicrophoneDeviceService microphoneDeviceService,
        ITelemetryService telemetry,
        ITranscriptModelService transcriptModelService)
    {
        _transcriptModelService = transcriptModelService;
        _transcriptModelInstalled = transcriptModelService.IsModelInstalled;
        _dialogService = dialogService;
        _microphoneDeviceService = microphoneDeviceService;
        _settingsService = settingsService;
        _telemetry = telemetry;
        _themeService = themeService;
        _availableMicrophoneDevices = microphoneDeviceService.GetAvailableCaptureDeviceNames();

        var s = settingsService.Current;
        _screenshotSavePath = s.ScreenshotSavePath;
        _autoSaveScreenshots = s.AutoSaveScreenshots;
        _recordingOutputPath = s.RecordingOutputPath;
        _recordMicrophone = s.RecordMicrophone;
        _recordingTranscriptEnabled = s.RecordingTranscriptEnabled;
        _selectedMicrophoneDeviceName = ResolveInitialMicrophoneDeviceName(s.RecordingMicrophoneDeviceName);
        _gifFps = s.GifFps;
        _recordingCursorHighlightEnabled = s.RecordingCursorHighlightEnabled;
        _recordingClickRippleEnabled = s.RecordingClickRippleEnabled;
        _recordingCursorHighlightSize = ClampRecordingCursorHighlightSize(s.RecordingCursorHighlightSize);
        _captureDelaySeconds = s.CaptureDelaySeconds;
        _shareDestinationUrl = s.ShareDestinationUrl ?? string.Empty;
        _shareFileFieldName = s.ShareFileFieldName ?? string.Empty;
        _shareResponseLinkPath = s.ShareResponseLinkPath ?? string.Empty;
        _shareTimeoutSeconds = s.ShareTimeoutSeconds;
        foreach (var header in s.ShareHeaders ?? [])
        {
            try
            {
                ShareHeaders.Add(new ShareHeaderEditor(header.Name, ShareHeaderProtection.Unprotect(header), header));
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException)
            {
                ShareHeaders.Add(new ShareHeaderEditor(header.Name, string.Empty));
            }
        }
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
        _watermarkHiddenStyle = watermark;
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

        TrackSectionEvent(TelemetryEvents.SettingsOpened, SelectedSection);
    }

    public IReadOnlyList<SettingsSectionItem> Sections => SectionItems;

    [ObservableProperty]
    private string _screenshotSavePath;

    [ObservableProperty]
    private bool _autoSaveScreenshots;

    [ObservableProperty]
    private string _recordingOutputPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEnableTranscript))]
    [NotifyPropertyChangedFor(nameof(TranscriptStatusText))]
    private bool _recordMicrophone;

    [ObservableProperty]
    private bool _recordingTranscriptEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEnableTranscript))]
    [NotifyPropertyChangedFor(nameof(ShowTranscriptModelDownload))]
    [NotifyPropertyChangedFor(nameof(TranscriptStatusText))]
    [NotifyCanExecuteChangedFor(nameof(DownloadTranscriptModelCommand))]
    private bool _transcriptModelInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TranscriptStatusText))]
    [NotifyCanExecuteChangedFor(nameof(DownloadTranscriptModelCommand))]
    private bool _isDownloadingTranscriptModel;

    [ObservableProperty]
    private double _transcriptModelDownloadProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TranscriptStatusText))]
    private bool _transcriptModelDownloadFailed;

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
    [NotifyPropertyChangedFor(nameof(ShareValidationMessage))]
    [NotifyPropertyChangedFor(nameof(IsShareSettingsValid))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _shareDestinationUrl = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShareValidationMessage))]
    [NotifyPropertyChangedFor(nameof(IsShareSettingsValid))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _shareFileFieldName = "file";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShareValidationMessage))]
    [NotifyPropertyChangedFor(nameof(IsShareSettingsValid))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _shareResponseLinkPath = "url";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShareValidationMessage))]
    [NotifyPropertyChangedFor(nameof(IsShareSettingsValid))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private int _shareTimeoutSeconds = 30;

    public ObservableCollection<ShareHeaderEditor> ShareHeaders { get; } = [];

    public bool IsShareSettingsValid => GetShareValidationMessage() is null;

    public string ShareValidationMessage => GetShareValidationMessage() ?? string.Empty;

    private string? GetShareValidationMessage()
    {
        var destination = (ShareDestinationUrl ?? string.Empty).Trim();
        if (destination.Length > 0)
        {
            if (!Uri.TryCreate(destination, UriKind.Absolute, out var uri)
                || !string.IsNullOrEmpty(uri.UserInfo)
                || !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
            {
                return "Enter an HTTPS URL. HTTP is allowed only for localhost or loopback addresses.";
            }
        }

        if (string.IsNullOrWhiteSpace(ShareFileFieldName))
        {
            return "Enter the multipart file field name.";
        }

        if (string.IsNullOrWhiteSpace(ShareResponseLinkPath))
        {
            return "Enter the JSON path that contains the returned link.";
        }

        if (ShareTimeoutSeconds is < 1 or > 300)
        {
            return "Timeout must be between 1 and 300 seconds.";
        }

        return null;
    }

    public sealed partial class ShareHeaderEditor : ObservableObject
    {
        private readonly ProtectedShareHeader? _original;

        public ShareHeaderEditor(string name = "", string value = "", ProtectedShareHeader? original = null)
        {
            _name = name;
            _value = value;
            _original = original;
        }

        public ProtectedShareHeader ToProtectedModel() =>
            _original is not null && _original.Name == Name.Trim() && ShareHeaderProtection.Unprotect(_original) == Value
                ? _original
                : ShareHeaderProtection.Protect(Name.Trim(), Value);

        [ObservableProperty]
        private string _name;

        [ObservableProperty]
        private string _value;
    }

    [RelayCommand]
    private void AddShareHeader() => ShareHeaders.Add(new ShareHeaderEditor());

    [RelayCommand]
    private void RemoveShareHeader(ShareHeaderEditor header) => ShareHeaders.Remove(header);

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
    [NotifyPropertyChangedFor(nameof(IsRecordingSectionSelected))]
    [NotifyPropertyChangedFor(nameof(IsAnnotationSectionSelected))]
    [NotifyPropertyChangedFor(nameof(IsShareSectionSelected))]
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
    public bool IsRecordingSectionSelected => SelectedSection == SettingsSection.Recording;
    public bool IsAnnotationSectionSelected => SelectedSection == SettingsSection.Annotation;
    public bool IsShareSectionSelected => SelectedSection == SettingsSection.Sharing;
    public bool IsShortcutsSectionSelected => SelectedSection == SettingsSection.Shortcuts;
    public bool IsAppSectionSelected => SelectedSection == SettingsSection.App;

    partial void OnDefaultAnnotationColorChanged(Color value) =>
        OnPropertyChanged(nameof(ColorPreviewBrush));

    partial void OnAppThemeChanged(AppTheme value) => _themeService.Apply(value);

    partial void OnSelectedSectionChanged(SettingsSection value)
    {
        TrackSectionEvent(TelemetryEvents.SettingsSectionChanged, value);
    }

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
            Color = ToArgbHex(DefaultAnnotationColor),
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

    [RelayCommand(CanExecute = nameof(IsShareSettingsValid))]
    private void Save()
    {
        OnPropertyChanged(nameof(IsShareSettingsValid));
        if (!IsShareSettingsValid)
        {
            return;
        }

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
            RecordingTranscriptEnabled = RecordingTranscriptEnabled,
            RecordingFps = _recordingFps,
            GifFps = GifFps,
            RecordingCursorHighlightEnabled = RecordingCursorHighlightEnabled,
            RecordingClickRippleEnabled = RecordingClickRippleEnabled,
            RecordingCursorHighlightSize = clampedRecordingCursorHighlightSize,
            CaptureDelaySeconds = CaptureDelaySeconds,
            ShareDestinationUrl = (ShareDestinationUrl ?? string.Empty).Trim(),
            ShareFileFieldName = (ShareFileFieldName ?? string.Empty).Trim(),
            ShareHeaders = [.. ShareHeaders.Select(header => header.ToProtectedModel())],
            ShareResponseLinkPath = (ShareResponseLinkPath ?? string.Empty).Trim(),
            ShareTimeoutSeconds = ShareTimeoutSeconds,
            HudGapPixels = _hudGapPixels,
            ScreenshotWatermark = BuildWatermark<ScreenshotWatermarkSettings>(),
            VideoWatermark = BuildWatermark<VideoWatermarkSettings>(),
            DefaultAnnotationColor = ToArgbHex(DefaultAnnotationColor),
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
        TrackSectionEvent(TelemetryEvents.SettingsSaved, SelectedSection);
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void StartRecordingHotkey()
    {
        IsCapturingWholeScreenRecordHotkey = false;
        IsCapturingCleanWindowCaptureHotkey = false;
        CancelCapturingOverlayShortcut();
        IsRecordingHotkey = true;
    }

    [RelayCommand]
    private void ResetHotkey()
    {
        var defaults = new UserSettings();
        RegionCaptureHotkey = defaults.RegionCaptureHotkey;
        RegionCaptureHotkeyModifiers = defaults.RegionCaptureHotkeyModifiers;
        IsRecordingHotkey = false;
    }

    [RelayCommand]
    private void StartCapturingWholeScreenRecordHotkey()
    {
        IsRecordingHotkey = false;
        IsCapturingCleanWindowCaptureHotkey = false;
        CancelCapturingOverlayShortcut();
        IsCapturingWholeScreenRecordHotkey = true;
    }

    [RelayCommand]
    private void StartCapturingCleanWindowCaptureHotkey()
    {
        IsRecordingHotkey = false;
        CancelCapturingOverlayShortcut();
        IsCapturingWholeScreenRecordHotkey = false;
        IsCapturingCleanWindowCaptureHotkey = true;
    }

    [RelayCommand]
    private void ResetRecordHotkey()
    {
        var defaults = new UserSettings();
        WholeScreenRecordHotkey = defaults.WholeScreenRecordHotkey;
        WholeScreenRecordHotkeyModifiers = defaults.WholeScreenRecordHotkeyModifiers;
        IsCapturingWholeScreenRecordHotkey = false;
    }

    [RelayCommand]
    private void ResetCleanWindowCaptureHotkey()
    {
        var defaults = new UserSettings();
        CleanWindowCaptureHotkey = defaults.CleanWindowCaptureHotkey;
        CleanWindowCaptureHotkeyModifiers = defaults.CleanWindowCaptureHotkeyModifiers;
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
        TrackSectionEvent(TelemetryEvents.SettingsSectionReset, SelectedSection);

        var defaults = new UserSettings();
        switch (SelectedSection)
        {
            case SettingsSection.Capture:
                ResetCaptureSection(defaults);
                break;
            case SettingsSection.Recording:
                ResetRecordingSection(defaults);
                break;
            case SettingsSection.Annotation:
                ResetAnnotationSection(defaults);
                break;
            case SettingsSection.Sharing:
                ResetShareSettings(defaults);
                break;
            case SettingsSection.App:
                ResetAppSection(defaults);
                break;
            case SettingsSection.Shortcuts:
                ResetShortcutsSection(defaults);
                break;
        }
    }

    [RelayCommand]
    private void RestoreDefaults()
    {
        _telemetry.TrackEvent(TelemetryEvents.SettingsDefaultsRestored);

        // Values the window does not show are reset here directly; see lessons.md,
        // "Restore-defaults flows must update hidden persisted settings directly".
        var defaults = new UserSettings();
        _recordingFps = defaults.RecordingFps;
        _hudGapPixels = defaults.HudGapPixels;
        _lastAutoUpdateCheckUtc = defaults.LastAutoUpdateCheckUtc;
        ResetCaptureSection(defaults);
        ResetShareSettings(defaults);
        ResetRecordingSection(defaults);
        ResetAnnotationSection(defaults);
        ResetShortcutsSection(defaults);
        ResetAppSection(defaults);
    }

    private void ResetCaptureSection(UserSettings defaults)
    {
        ScreenshotSavePath = defaults.ScreenshotSavePath;
        AutoSaveScreenshots = defaults.AutoSaveScreenshots;
        CaptureDelaySeconds = defaults.CaptureDelaySeconds;
        WatermarkEnabled = defaults.ScreenshotWatermark.Enabled;
        WatermarkTextTemplate = defaults.ScreenshotWatermark.TextTemplate;
        WatermarkPosition = defaults.ScreenshotWatermark.Position;
        WatermarkFontSize = defaults.ScreenshotWatermark.FontSize;
        WatermarkApplyToCopy = defaults.ScreenshotWatermark.ApplyToCopy;
        WatermarkApplyToSave = defaults.ScreenshotWatermark.ApplyToSave;
        _watermarkHiddenStyle = defaults.ScreenshotWatermark;
        RegionCaptureHotkey = defaults.RegionCaptureHotkey;
        RegionCaptureHotkeyModifiers = defaults.RegionCaptureHotkeyModifiers;
        IsRecordingHotkey = false;
    }

    private void ResetRecordingSection(UserSettings defaults)
    {
        RecordingOutputPath = defaults.RecordingOutputPath;
        RecordMicrophone = defaults.RecordMicrophone;
        RecordingTranscriptEnabled = defaults.RecordingTranscriptEnabled;
        SelectedMicrophoneDeviceName = ResolveInitialMicrophoneDeviceName(defaults.RecordingMicrophoneDeviceName);
        GifFps = defaults.GifFps;
        RecordingCursorHighlightEnabled = defaults.RecordingCursorHighlightEnabled;
        RecordingClickRippleEnabled = defaults.RecordingClickRippleEnabled;
        RecordingCursorHighlightSize = ClampRecordingCursorHighlightSize(defaults.RecordingCursorHighlightSize);
        WholeScreenRecordHotkey = defaults.WholeScreenRecordHotkey;
        WholeScreenRecordHotkeyModifiers = defaults.WholeScreenRecordHotkeyModifiers;
        IsCapturingWholeScreenRecordHotkey = false;
        CleanWindowCaptureHotkey = defaults.CleanWindowCaptureHotkey;
        CleanWindowCaptureHotkeyModifiers = defaults.CleanWindowCaptureHotkeyModifiers;
        IsCapturingCleanWindowCaptureHotkey = false;
    }

    private void ResetAnnotationSection(UserSettings defaults)
    {
        DefaultAnnotationColor = ParseAnnotationColorOrFallback(defaults.DefaultAnnotationColor);
        DefaultStrokeThickness = defaults.DefaultStrokeThickness;
        ResetStylePresets(defaults.StylePresets);
    }

    private void ResetShareSettings(UserSettings defaults)
    {
        ShareDestinationUrl = defaults.ShareDestinationUrl;
        ShareFileFieldName = defaults.ShareFileFieldName;
        ShareResponseLinkPath = defaults.ShareResponseLinkPath;
        ShareTimeoutSeconds = defaults.ShareTimeoutSeconds;
        ShareHeaders.Clear();
    }

    private void ResetShortcutsSection(UserSettings defaults)
    {
        ResetOverlayShortcutsTo(defaults);
        CancelCapturingOverlayShortcut();
    }

    private void ResetAppSection(UserSettings defaults)
    {
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

    private void ResetOverlayShortcutsTo(UserSettings settings)
    {
        foreach (var descriptor in OverlayShortcutDescriptors)
        {
            descriptor.Set(this, descriptor.SettingOf(settings));
        }
    }

    private static string OverlayShortcutLabel(string shortcutKey) =>
        FindOverlayShortcut(shortcutKey)?.Label ?? "Shortcut";

    private TWatermark BuildWatermark<TWatermark>()
        where TWatermark : WatermarkSettings, new()
    {
        return new TWatermark
        {
            Enabled = WatermarkEnabled,
            TextTemplate = WatermarkTextTemplate,
            Position = WatermarkPosition,
            FontSize = WatermarkFontSize,
            ApplyToCopy = WatermarkApplyToCopy,
            ApplyToSave = WatermarkApplyToSave,
            ColorHex = _watermarkHiddenStyle.ColorHex,
            BackgroundEnabled = _watermarkHiddenStyle.BackgroundEnabled,
            Opacity = _watermarkHiddenStyle.Opacity,
            Margin = _watermarkHiddenStyle.Margin,
        };
    }

    private static string ToArgbHex(Color color) => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    private void TrackSectionEvent(string eventName, SettingsSection section)
    {
        _telemetry.TrackEvent(eventName, new Dictionary<string, string>
        {
            [TelemetryPropertyKeys.AppSection] = section.ToString().ToLowerInvariant(),
        });
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

    public bool CanEnableTranscript => RecordMicrophone && TranscriptModelInstalled;

    public bool CanToggleTranscript => RecordMicrophone;

    public bool ShowTranscriptModelDownload => !TranscriptModelInstalled;

    public string TranscriptStatusText
    {
        get
        {
            if (IsDownloadingTranscriptModel)
            {
                return "Downloading the English speech model…";
            }

            if (TranscriptModelDownloadFailed)
            {
                return "The download failed. Check your internet connection and try again.";
            }

            if (!TranscriptModelInstalled)
            {
                return "The English speech model is not installed yet (about 141 MB).";
            }

            if (!RecordMicrophone)
            {
                return "Turn on \"Include microphone audio\" to transcribe your narration.";
            }

            return "Ready. Transcripts are generated on this machine — nothing is uploaded.";
        }
    }

    private bool CanDownloadTranscriptModel() => !TranscriptModelInstalled && !IsDownloadingTranscriptModel;

    [RelayCommand(CanExecute = nameof(CanDownloadTranscriptModel))]
    private async Task DownloadTranscriptModel()
    {
        IsDownloadingTranscriptModel = true;
        TranscriptModelDownloadFailed = false;
        TranscriptModelDownloadProgress = 0;

        try
        {
            var progress = new Progress<double>(percent => TranscriptModelDownloadProgress = percent);
            var downloaded = await _transcriptModelService.DownloadModel(progress);

            TranscriptModelInstalled = _transcriptModelService.IsModelInstalled;
            TranscriptModelDownloadFailed = !downloaded && !TranscriptModelInstalled;
        }
        catch (OperationCanceledException)
        {
            TranscriptModelInstalled = _transcriptModelService.IsModelInstalled;
        }
        finally
        {
            IsDownloadingTranscriptModel = false;
        }
    }

}
