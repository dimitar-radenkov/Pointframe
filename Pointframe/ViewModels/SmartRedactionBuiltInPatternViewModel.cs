namespace Pointframe.ViewModels;

public sealed partial class SmartRedactionBuiltInPatternViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isEnabled;

    public SmartRedactionBuiltInPatternViewModel(
        SensitiveDataType type,
        string name,
        string description,
        string example,
        string pattern,
        bool isEnabled)
    {
        Type = type;
        Name = name;
        Description = description;
        Example = example;
        Pattern = pattern;
        _isEnabled = isEnabled;
    }

    public SensitiveDataType Type { get; }

    public string Name { get; }

    public string Description { get; }

    public string Example { get; }

    public string Pattern { get; }
}

