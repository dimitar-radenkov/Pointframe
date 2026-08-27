namespace Pointframe.ViewModels;

public sealed partial class SmartRedactionPatternViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _pattern;

    [ObservableProperty]
    private bool _isEnabled;

    public SmartRedactionPatternViewModel(SmartRedactionPattern pattern)
    {
        _name = pattern.Name;
        _pattern = pattern.Pattern;
        _isEnabled = pattern.IsEnabled;
    }

    public SmartRedactionPattern ToModel()
    {
        return new SmartRedactionPattern
        {
            Name = Name,
            Pattern = Pattern,
            IsEnabled = IsEnabled,
        };
    }
}
