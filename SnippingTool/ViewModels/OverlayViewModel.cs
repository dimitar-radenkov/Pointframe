using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SnippingTool.Services;
using SnippingTool.Services.Messaging;

namespace SnippingTool.ViewModels;

public partial class OverlayViewModel : AnnotationViewModel
{
    private readonly IClipboardService _clipboardService;
    private readonly IDialogService _dialogService;
    private readonly IFileSystemService _fileSystemService;
    private readonly IUserSettingsService _settings;
    private IOverlayBitmapCapture? _bitmapCapture;

    public OverlayViewModel(
        IAnnotationGeometryService geometry,
        ILogger<OverlayViewModel> logger,
        IUserSettingsService settings,
        IDialogService dialogService,
        IClipboardService clipboardService,
        IFileSystemService fileSystemService,
        IEventAggregator eventAggregator)
        : base(geometry, logger, settings, eventAggregator)
    {
        _clipboardService = clipboardService;
        _dialogService = dialogService;
        _fileSystemService = fileSystemService;
        _settings = settings;
    }

    public enum Phase { Selecting, Annotating }

    [ObservableProperty]
    private Phase _currentPhase = Phase.Selecting;

    partial void OnCurrentPhaseChanged(Phase value) =>
        _logger.LogDebug("Phase transition: {Phase}", value);

    [ObservableProperty]
    private Rect _selectionRect = Rect.Empty;

    public Int32Rect SelectionScreenBoundsPixels { get; private set; } = Int32Rect.Empty;

    public double DpiX { get; set; } = 1.0;
    public double DpiY { get; set; } = 1.0;

    [ObservableProperty]
    private string _sizeLabel = string.Empty;

    [ObservableProperty]
    private bool _isTextLassoActive;

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
            var saveDirectory = _settings.Current.ScreenshotSavePath;
            _fileSystemService.CreateDirectory(saveDirectory);
            var savePath = _fileSystemService.CombinePath(saveDirectory, $"Snip_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            using var outputStream = _fileSystemService.OpenWrite(savePath);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(finalBitmap));
            encoder.Save(outputStream);
        }

        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void PickColor()
    {
        var selectedColor = _dialogService.PickColor(ActiveColor);
        if (selectedColor.HasValue)
        {
            ActiveColor = selectedColor.Value;
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

        PinRequested?.Invoke(bitmapCapture.ComposeBitmap(restoreOverlayVisibilityAfterCapture: false));
    }
}
