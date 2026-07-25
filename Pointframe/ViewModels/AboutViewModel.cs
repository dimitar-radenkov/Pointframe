using System.Diagnostics;
using Pointframe.Services;

namespace Pointframe.ViewModels;

public partial class AboutViewModel : ObservableObject
{
    private readonly IProcessService _process;
    private readonly ITelemetryService _telemetry;
    public string Version { get; }

    public event Action? RequestClose;

    public AboutViewModel(IAppVersionService appVersion, IProcessService process)
        : this(appVersion, process, NullTelemetryService.Instance)
    {
    }

    public AboutViewModel(IAppVersionService appVersion, IProcessService process, ITelemetryService telemetry)
    {
        _process = process;
        _telemetry = telemetry;
        var v = appVersion.Current;
        Version = $"Version {v.Major}.{v.Minor}.{v.Build}";
        _telemetry.TrackEvent(TelemetryEvents.AboutOpened);
    }

    [RelayCommand]
    private void OpenUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && !string.IsNullOrWhiteSpace(uri.Host))
        {
            _telemetry.TrackEvent(TelemetryEvents.AboutUrlOpened, new Dictionary<string, string>
            {
                [TelemetryPropertyKeys.UrlHost] = uri.Host,
            });
        }

        _process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    [RelayCommand]
    private void Close()
    {
        _telemetry.TrackEvent(TelemetryEvents.AboutClosed);
        RequestClose?.Invoke();
    }
}
