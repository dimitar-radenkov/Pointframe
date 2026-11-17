namespace Pointframe.ViewModels;

public sealed partial class CameraDeviceOption : ObservableObject
{
    public CameraDeviceOption(string id, string name)
    {
        Id = id;
        _name = name;
    }

    // Empty means the system default camera.
    public string Id { get; }

    [ObservableProperty]
    private string _name;
}
