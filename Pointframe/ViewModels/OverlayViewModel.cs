using System.Windows;
using Pointframe.Services;
using Pointframe.Services.Messaging;

namespace Pointframe.ViewModels;

public partial class OverlayViewModel : AnnotationViewModel
{
    private readonly IClipboardService _clipboardService;
    private readonly IDialogService _dialogService;
    private readonly IFileSystemService _fileSystemService;
    private readonly IUserSettingsService _settings;
    private readonly IEventAggregator _eventAggregator;
    private readonly ITelemetryService _telemetry;
    private readonly IScreenshotWatermarkService _watermarkService;
    private IOverlayBitmapCapture? _bitmapCapture;

    public OverlayViewModel(
        IAnnotationGeometryService geometry,
        ILogger<OverlayViewModel> logger,
        IUserSettingsService settings,
        IDialogService dialogService,
        IClipboardService clipboardService,
        IFileSystemService fileSystemService,
        IEventAggregator eventAggregator,
        ITelemetryService telemetry,
        IScreenshotWatermarkService watermarkService)
        : base(geometry, logger, settings, eventAggregator, telemetry)
    {
        _clipboardService = clipboardService;
        _dialogService = dialogService;
        _fileSystemService = fileSystemService;
        _settings = settings;
        _eventAggregator = eventAggregator;
        _telemetry = telemetry;
        _watermarkService = watermarkService;
    }

    public enum Phase { Selecting, Annotating }

    [ObservableProperty]
    private Phase _currentPhase = Phase.Selecting;

    partial void OnCurrentPhaseChanged(Phase value) =>
        _logger.LogDebug("Phase transition: {Phase}", value);

    [ObservableProperty]
    private Rect _selectionRect = Rect.Empty;

    public Int32Rect SelectionScreenBoundsPixels { get; private set; } = Int32Rect.Empty;

    [ObservableProperty]
    private string _sizeLabel = string.Empty;

    [ObservableProperty]
    private bool _isTextLassoActive;

    public string OverlayCopyHotkeyDisplayName => new HotkeyBinding(_settings.Current.OverlayCopyHotkey, _settings.Current.OverlayCopyHotkeyModifiers).DisplayName;
    public string OverlaySaveAsHotkeyDisplayName => new HotkeyBinding(_settings.Current.OverlaySaveAsHotkey, _settings.Current.OverlaySaveAsHotkeyModifiers).DisplayName;
    public string OverlayUndoHotkeyDisplayName => new HotkeyBinding(_settings.Current.OverlayUndoHotkey, _settings.Current.OverlayUndoHotkeyModifiers).DisplayName;
    public string OverlayRedoHotkeyDisplayName => new HotkeyBinding(_settings.Current.OverlayRedoHotkey, _settings.Current.OverlayRedoHotkeyModifiers).DisplayName;
    public string OverlayToggleShortcutsHotkeyDisplayName => new HotkeyBinding(_settings.Current.OverlayToggleShortcutsHotkey, _settings.Current.OverlayToggleShortcutsHotkeyModifiers).DisplayName;
    public string OverlayCloseHotkeyDisplayName => new HotkeyBinding(_settings.Current.OverlayCloseHotkey, _settings.Current.OverlayCloseHotkeyModifiers).DisplayName;
    public bool IsSmartRedactionEnabled => _settings.Current.SmartRedactionEnabled;

    public string CopyToolTip => $"Copy to clipboard ({OverlayCopyHotkeyDisplayName})";
    public string SaveAsToolTip => $"Save As ({OverlaySaveAsHotkeyDisplayName})";
    public string UndoToolTip => $"Undo ({OverlayUndoHotkeyDisplayName})";
    public string RedoToolTip => $"Redo ({OverlayRedoHotkeyDisplayName})";
    public string CloseToolTip => $"Close ({OverlayCloseHotkeyDisplayName})";

    public string PopupToggleShortcutsText => $"{OverlayToggleShortcutsHotkeyDisplayName}: Toggle shortcuts";
    public string PopupCopyText => $"{OverlayCopyHotkeyDisplayName}: Copy";
    public string PopupSaveAsText => $"{OverlaySaveAsHotkeyDisplayName}: Save As";
    public string PopupUndoText => $"{OverlayUndoHotkeyDisplayName}: Undo";
    public string PopupRedoText => $"{OverlayRedoHotkeyDisplayName}: Redo";
    public string PopupCloseText => $"{OverlayCloseHotkeyDisplayName}: Close";

    public void InitializeAnnotatingSession(Rect selection, double pixelScaleX, double pixelScaleY)
    {
        SelectionRect = selection;
        DpiX = pixelScaleX;
        DpiY = pixelScaleY;
        CurrentPhase = Phase.Annotating;
    }

    public void CommitSelection(Rect selection)
    {
        SelectionScreenBoundsPixels = Int32Rect.Empty;
        InitializeAnnotatingSession(selection, DpiX, DpiY);
        _logger.LogInformation("Selection committed: {W:F0}\u00d7{H:F0} at ({X:F0},{Y:F0})",
            selection.Width, selection.Height, selection.X, selection.Y);
    }

    public void CommitSelection(Rect selection, Int32Rect selectionScreenBoundsPixels)
    {
        SelectionScreenBoundsPixels = selectionScreenBoundsPixels;
        InitializeAnnotatingSession(
            selection,
            selection.Width > 0d ? selectionScreenBoundsPixels.Width / selection.Width : DpiX,
            selection.Height > 0d ? selectionScreenBoundsPixels.Height / selection.Height : DpiY);
        _logger.LogInformation("Selection committed: {W:F0}\u00d7{H:F0} at ({X:F0},{Y:F0})",
            selection.Width, selection.Height, selection.X, selection.Y);
    }

    public void UpdateSizeLabel(double w, double h) =>
        SizeLabel = $"{(int)(w * DpiX)}×{(int)(h * DpiY)}";

    public event Action? CloseRequested;
    public event Action<BitmapSource>? PinRequested;
    public event Action<BitmapSource>? BeautifyRequested;

    internal void SetBitmapCapture(IOverlayBitmapCapture bitmapCapture)
    {
        _bitmapCapture = bitmapCapture;
    }

    [RelayCommand]
    private void Copy()
    {
        var bitmapCapture = _bitmapCapture;
        if (bitmapCapture is null)
        {
            _logger.LogWarning("Copy requested before overlay bitmap capture was attached");
            return;
        }

        var finalBitmap = bitmapCapture.ComposeBitmap();
        _clipboardService.SetImage(ApplyWatermarkForCopy(finalBitmap));
        _ = _eventAggregator.Publish(new CaptureCompletedMessage(null, "copy"));

        if (_settings.Current.AutoSaveScreenshots)
        {
            _ = SaveBitmapToDefaultFolder(ApplyWatermarkForSave(finalBitmap), "auto_save");
        }

        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Save()
    {
        var bitmapCapture = _bitmapCapture;
        if (bitmapCapture is null)
        {
            _logger.LogWarning("Save requested before overlay bitmap capture was attached");
            return;
        }

        var finalBitmap = bitmapCapture.ComposeBitmap();
        _ = SaveBitmapToDefaultFolder(ApplyWatermarkForSave(finalBitmap), "save");
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void SaveAs()
    {
        var bitmapCapture = _bitmapCapture;
        if (bitmapCapture is null)
        {
            _logger.LogWarning("Save As requested before overlay bitmap capture was attached");
            return;
        }

        var finalBitmap = bitmapCapture.ComposeBitmap();
        var saveDirectory = _settings.Current.ScreenshotSavePath;
        _fileSystemService.CreateDirectory(saveDirectory);

        var suggestedFileName = $"Snip_{DateTime.Now:yyyyMMdd_HHmmss}.png";
        var savePath = _dialogService.PickSaveImageFile(saveDirectory, suggestedFileName);
        if (string.IsNullOrWhiteSpace(savePath))
        {
            return;
        }

        SaveBitmapToPath(ApplyWatermarkForSave(finalBitmap), savePath, "save_as");
        CloseRequested?.Invoke();
    }

    private BitmapSource ApplyWatermarkForCopy(BitmapSource bitmap)
    {
        var watermark = _settings.Current.ScreenshotWatermark;
        if (watermark is { Enabled: true, ApplyToCopy: true })
        {
            return _watermarkService.Apply(bitmap, watermark);
        }

        return bitmap;
    }

    private BitmapSource ApplyWatermarkForSave(BitmapSource bitmap)
    {
        var watermark = _settings.Current.ScreenshotWatermark;
        if (watermark is { Enabled: true, ApplyToSave: true })
        {
            return _watermarkService.Apply(bitmap, watermark);
        }

        return bitmap;
    }

    private string SaveBitmapToDefaultFolder(BitmapSource bitmap, string captureAction)
    {
        var saveDirectory = _settings.Current.ScreenshotSavePath;
        _fileSystemService.CreateDirectory(saveDirectory);
        var savePath = _fileSystemService.CombinePath(saveDirectory, $"Snip_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        SaveBitmapToPath(bitmap, savePath, captureAction);
        return savePath;
    }

    private void SaveBitmapToPath(BitmapSource bitmap, string savePath, string captureAction)
    {
        using var outputStream = _fileSystemService.OpenWrite(savePath);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(outputStream);
        _ = _eventAggregator.Publish(new CaptureCompletedMessage(savePath, captureAction));
    }

    [RelayCommand]
    private void PickColor()
    {
        IsColorMenuOpen = false;
        var selectedColor = _dialogService.PickColor(ActiveColor);
        if (selectedColor.HasValue)
        {
            ActiveColor = selectedColor.Value;
            ActivePresetIndex = null;
        }
    }

    [RelayCommand]
    private void CopyText() => IsTextLassoActive = !IsTextLassoActive;

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    [RelayCommand]
    private void Pin()
    {
        var bitmapCapture = _bitmapCapture;
        if (bitmapCapture is null)
        {
            _logger.LogWarning("Pin requested before overlay bitmap capture was attached");
            return;
        }

        _telemetry.TrackEvent(TelemetryEvents.CapturePinned);
        PinRequested?.Invoke(bitmapCapture.ComposeBitmap(restoreOverlayVisibilityAfterCapture: false));
    }

    [RelayCommand]
    private void Beautify()
    {
        var bitmapCapture = _bitmapCapture;
        if (bitmapCapture is null)
        {
            _logger.LogWarning("Beautify requested before overlay bitmap capture was attached");
            return;
        }

        _telemetry.TrackEvent(TelemetryEvents.BeautifyOpened);
        BeautifyRequested?.Invoke(bitmapCapture.ComposeBitmap(restoreOverlayVisibilityAfterCapture: false));
    }
}
