using System.ComponentModel;
using System.Windows.Media;

namespace Pointframe.ViewModels;

/// <summary>
/// Read-only overlay item representing one style preset dot in the annotation toolbar.
/// </summary>
public sealed partial class AnnotationPresetItemViewModel : ObservableObject
{
    private readonly AnnotationViewModel _parent;

    public AnnotationPresetItemViewModel(int index, string name, string color, double strokeThickness, SolidColorBrush brush, AnnotationViewModel parent)
    {
        Index = index;
        Name = name;
        Color = color;
        StrokeThickness = strokeThickness;
        Brush = brush;
        _parent = parent;
        _parent.PropertyChanged += OnParentPropertyChanged;
    }

    public int Index { get; }
    public string Name { get; }
    public string Color { get; }
    public double StrokeThickness { get; }
    public SolidColorBrush Brush { get; }
    public bool IsActive => _parent.ActivePresetIndex == Index;

    private void OnParentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AnnotationViewModel.ActivePresetIndex))
        {
            OnPropertyChanged(nameof(IsActive));
        }
    }
}
