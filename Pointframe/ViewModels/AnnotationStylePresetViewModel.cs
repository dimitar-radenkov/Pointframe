using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Pointframe.ViewModels;

/// <summary>
/// Editable preset row used in the Settings window Annotation section.
/// </summary>
public sealed partial class AnnotationStylePresetViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ColorBrush))]
    private Color _color;

    [ObservableProperty]
    private double _strokeThickness;

    public SolidColorBrush ColorBrush => new(Color);

    public AnnotationStylePresetViewModel(AnnotationStylePreset preset)
    {
        _name = preset.Name;
        _color = ParseColor(preset.Color);
        _strokeThickness = preset.StrokeThickness;
    }

    public AnnotationStylePreset ToModel()
    {
        var c = Color;
        return new AnnotationStylePreset
        {
            Name = Name,
            Color = $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}",
            StrokeThickness = StrokeThickness,
        };
    }

    internal static Color ParseColor(string hex)
    {
        try
        {
            return (Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        }
        catch
        {
            return Colors.Red;
        }
    }
}
