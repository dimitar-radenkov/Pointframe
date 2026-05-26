using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
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
    private IOverlayBitmapCapture? _bitmapCapture;

    public OverlayViewModel(
        IAnnotationGeometryService geometry,
        ILogger<OverlayViewModel> logger,
        IUserSettingsService settings,
        IDialogService dialogService,
        IClipboardService clipboardService,
        IFileSystemService fileSystemService,
        IEventAggregator eventAggregator,
        ITelemetryService telemetry)
        : base(geometry, logger, settings, eventAggregator, telemetry)
    {
        _clipboardService = clipboardService;
        _dialogService = dialogService;
        _fileSystemService = fileSystemService;
        _settings = settings;
        _eventAggregator = eventAggregator;
        _telemetry = telemetry;
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

    public string OverlayCopyHotkeyDisplayName => BuildHotkeyDisplayName(_settings.Current.OverlayCopyHotkey, _settings.Current.OverlayCopyHotkeyModifiers);
    public string OverlaySaveAsHotkeyDisplayName => BuildHotkeyDisplayName(_settings.Current.OverlaySaveAsHotkey, _settings.Current.OverlaySaveAsHotkeyModifiers);
    public string OverlayUndoHotkeyDisplayName => BuildHotkeyDisplayName(_settings.Current.OverlayUndoHotkey, _settings.Current.OverlayUndoHotkeyModifiers);
    public string OverlayRedoHotkeyDisplayName => BuildHotkeyDisplayName(_settings.Current.OverlayRedoHotkey, _settings.Current.OverlayRedoHotkeyModifiers);
    public string OverlayToggleShortcutsHotkeyDisplayName => BuildHotkeyDisplayName(_settings.Current.OverlayToggleShortcutsHotkey, _settings.Current.OverlayToggleShortcutsHotkeyModifiers);
    public string OverlayCloseHotkeyDisplayName => BuildHotkeyDisplayName(_settings.Current.OverlayCloseHotkey, _settings.Current.OverlayCloseHotkeyModifiers);

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
        _clipboardService.SetImage(finalBitmap);

        if (_settings.Current.AutoSaveScreenshots)
        {
            _ = SaveBitmapToDefaultFolder(finalBitmap);
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
        _ = SaveBitmapToDefaultFolder(finalBitmap);
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

        SaveBitmapToPath(finalBitmap, savePath);
        CloseRequested?.Invoke();
    }

    private string SaveBitmapToDefaultFolder(BitmapSource bitmap)
    {
        var saveDirectory = _settings.Current.ScreenshotSavePath;
        _fileSystemService.CreateDirectory(saveDirectory);
        var savePath = _fileSystemService.CombinePath(saveDirectory, $"Snip_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        SaveBitmapToPath(bitmap, savePath);
        return savePath;
    }

    private void SaveBitmapToPath(BitmapSource bitmap, string savePath)
    {
        using var outputStream = _fileSystemService.OpenWrite(savePath);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(outputStream);
        _ = _eventAggregator.Publish(new CaptureCompletedMessage(savePath));
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

        parts.Add(vk == 0x2C ? "Print Screen" : KeyInterop.KeyFromVirtualKey((int)vk).ToString());
        return string.Join("+", parts);
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

        _telemetry.TrackEvent("capture_pinned");
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

        _telemetry.TrackEvent("beautify_opened");
        BeautifyRequested?.Invoke(bitmapCapture.ComposeBitmap(restoreOverlayVisibilityAfterCapture: false));
    }
}
