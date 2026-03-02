using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SnippingTool.ViewModels;

public partial class OverlayViewModel : AnnotationViewModel
{
    public enum Phase { Selecting, Annotating }

    [ObservableProperty]
    private Phase _currentPhase = Phase.Selecting;

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
