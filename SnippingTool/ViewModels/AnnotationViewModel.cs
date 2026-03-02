using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Color = System.Windows.Media.Color;

namespace SnippingTool.ViewModels;

public partial class AnnotationViewModel : ObservableObject
{
    [ObservableProperty]
    private AnnotationTool _selectedTool = AnnotationTool.Arrow;

    [ObservableProperty]
    private Color _activeColor = Colors.Red;

    [ObservableProperty]
    private double _strokeThickness = 2.5;

    public SolidColorBrush ActiveBrush => new(ActiveColor);

    partial void OnActiveColorChanged(Color value) => OnPropertyChanged(nameof(ActiveBrush));
}
