using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SnippingTool.Services;

namespace SnippingTool.ViewModels;

public partial class OverlayViewModel : AnnotationViewModel
{
    public OverlayViewModel(IAnnotationGeometryService geometry, ILogger<OverlayViewModel> logger)
        : base(geometry, logger) { }

    public enum Phase { Selecting, Annotating }

    [ObservableProperty]
    private Phase _currentPhase = Phase.Selecting;

    partial void OnCurrentPhaseChanged(Phase value) =>
        _logger.LogDebug("Phase transition: {Phase}", value);

    [ObservableProperty]
    private Rect _selectionRect = Rect.Empty;

    public double DpiX { get; set; } = 1.0;
    public double DpiY { get; set; } = 1.0;

    [ObservableProperty]
    private string _sizeLabel = string.Empty;

    public void CommitSelection(Rect selection)
    {
        SelectionRect = selection;
        CurrentPhase = Phase.Annotating;
        _logger.LogInformation("Selection committed: {W:F0}\u00d7{H:F0} at ({X:F0},{Y:F0})",
            selection.Width, selection.Height, selection.X, selection.Y);
    }

    public void UpdateSizeLabel(double w, double h) =>
        SizeLabel = $"{(int)(w * DpiX)}×{(int)(h * DpiY)}";

    public event Action? CopyRequested;
    public event Action? CloseRequested;

    [RelayCommand]
    private void Copy() => CopyRequested?.Invoke();

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();
}
