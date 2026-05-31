using System.Windows;
using Pointframe.Services;

namespace Pointframe.ViewModels;

public partial class BeautifierViewModel : ObservableObject
{
    private readonly IClipboardService _clipboardService;
    private readonly IFileSystemService _fileSystemService;
    private readonly IUserSettingsService _settings;
    private readonly ITelemetryService _telemetry;
    private readonly BeautifierRenderService _renderService;
    private readonly ILogger<BeautifierViewModel> _logger;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackgroundBrush))]
    private BeautifyBackground _selectedBackground = BeautifyBackground.Ocean;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PaddingThickness))]
    [NotifyPropertyChangedFor(nameof(CanvasPixelWidth))]
    [NotifyPropertyChangedFor(nameof(CanvasPixelHeight))]
    private double _padding = 40;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScreenshotCornerRadius))]
    private double _cornerRadius = 12;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveShadowOpacity))]
    private bool _shadowEnabled = true;

    [ObservableProperty]
    private double _shadowBlur = 40;

    [ObservableProperty]
    private double _shadowOffsetY = 20;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveShadowOpacity))]
    private double _shadowOpacity = 0.55;

    public BitmapSource SourceBitmap { get; private set; } = null!;

    public System.Windows.Media.Brush BackgroundBrush => BeautifyPresets.GetBrush(SelectedBackground);
    public Thickness PaddingThickness => new(Padding);
    public CornerRadius ScreenshotCornerRadius => new(CornerRadius);
    public double EffectiveShadowOpacity => ShadowEnabled ? ShadowOpacity : 0;
    public double CanvasPixelWidth => SourceBitmap is null ? 800 : SourceBitmap.PixelWidth + Padding * 2;
    public double CanvasPixelHeight => SourceBitmap is null ? 600 : SourceBitmap.PixelHeight + Padding * 2;

    public event Action? CloseRequested;
    public event Action<string>? ToastRequested;

    public BeautifierViewModel(
        IClipboardService clipboardService,
        IFileSystemService fileSystemService,
        IUserSettingsService settings,
        ITelemetryService telemetry,
        BeautifierRenderService renderService,
        ILogger<BeautifierViewModel> logger)
    {
        _clipboardService = clipboardService;
        _fileSystemService = fileSystemService;
        _settings = settings;
        _telemetry = telemetry;
        _renderService = renderService;
        _logger = logger;
    }

    internal void SetSourceBitmap(BitmapSource bitmap)
    {
        SourceBitmap = bitmap;
        OnPropertyChanged(nameof(SourceBitmap));
        OnPropertyChanged(nameof(CanvasPixelWidth));
        OnPropertyChanged(nameof(CanvasPixelHeight));
    }

    [RelayCommand]
    private void SelectBackground(BeautifyBackground preset) => SelectedBackground = preset;

    [RelayCommand]
    private void Export()
    {
        var bitmap = RenderCurrent();

        var saveDirectory = _settings.Current.ScreenshotSavePath;
        _fileSystemService.CreateDirectory(saveDirectory);
        var savePath = _fileSystemService.CombinePath(saveDirectory, $"Beautified_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        using var outputStream = _fileSystemService.OpenWrite(savePath);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(outputStream);

        _telemetry.TrackEvent("screenshot_beautified");
        _logger.LogInformation("Beautified screenshot saved: {Path}", savePath);
        ToastRequested?.Invoke($"Saved \u2014 {System.IO.Path.GetFileName(savePath)}");
    }

    [RelayCommand]
    private void CopyToClipboard()
    {
        var bitmap = RenderCurrent();
        _clipboardService.SetImage(bitmap);
        _telemetry.TrackEvent("screenshot_beautified_copied");
        ToastRequested?.Invoke("Copied to clipboard");
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    private BitmapSource RenderCurrent() => _renderService.Render(
        SourceBitmap, SelectedBackground, Padding, CornerRadius,
        ShadowEnabled, ShadowBlur, ShadowOffsetY, ShadowOpacity);
}
