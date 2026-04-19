using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pointframe.Services;

namespace Pointframe.ViewModels;

public partial class AboutViewModel : ObservableObject
{
    private readonly IProcessService _process;
    public string Version { get; }

    public event Action? RequestClose;

    public AboutViewModel(IAppVersionService appVersion, IProcessService process)
    {
        _process = process;
        var v = appVersion.Current;
        Version = $"Version {v.Major}.{v.Minor}.{v.Build}";
    }

    [RelayCommand]
    private void OpenUrl(string url) =>
        _process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    [RelayCommand]
    private void Close() => RequestClose?.Invoke();
}
