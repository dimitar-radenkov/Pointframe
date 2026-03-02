using System.IO;
using System.Windows;
using Microsoft.Extensions.Logging;
using SnippingTool.Services;

namespace SnippingTool;

public partial class RecordingHudWindow : Window
{
    private readonly IScreenRecordingService _svc;
    private readonly string _outputPath;
    private readonly ILogger<RecordingHudWindow> _logger;
    private CancellationTokenSource? _elapsedCts;
    private DateTime _startTime;

    private readonly Rect _regionRect;

    public event Action? StopCompleted;

    public RecordingHudWindow(IScreenRecordingService svc, string outputPath, ILogger<RecordingHudWindow> logger, Rect regionRect)
    {
        _svc = svc;
        _outputPath = outputPath;
        _logger = logger;
        _regionRect = regionRect;
        InitializeComponent();
        _logger.LogDebug("RecordingHudWindow created for path={Path}", outputPath);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _logger.LogDebug("RecordingHudWindow.OnSourceInitialized — starting elapsed timer");
        _startTime = DateTime.UtcNow;
        _elapsedCts = new CancellationTokenSource();
        _ = RunElapsedTimerAsync(_elapsedCts.Token);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        // Centre the HUD below the recorded region, clamped inside the work area.
        var wa = SystemParameters.WorkArea;
        Left = Math.Max(wa.Left, Math.Min(_regionRect.Left + (_regionRect.Width - ActualWidth) / 2, wa.Right - ActualWidth));
        Top = Math.Min(_regionRect.Bottom + 8, wa.Bottom - ActualHeight);
        _logger.LogInformation("RecordingHudWindow rendered: ActualSize={W}x{H}, Position=({Left},{Top})",
            ActualWidth, ActualHeight, Left, Top);
    }

    private async Task RunElapsedTimerAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            var elapsed = DateTime.UtcNow - _startTime;
            await Dispatcher.InvokeAsync(() => ElapsedText.Text = elapsed.ToString(@"mm\:ss"));
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("Stop button clicked");
        _elapsedCts?.Cancel();
        StopBtn.IsEnabled = false;

        _svc.Stop();

        var fileName = Path.GetFileName(_outputPath);
        SavedText.Text = $"Saved → {fileName}";
        SavedText.Visibility = Visibility.Visible;
        _logger.LogInformation("Recording saved to {Path}", _outputPath);

        _ = CloseAfterDelayAsync();
    }

    private async Task CloseAfterDelayAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(2));
        await Dispatcher.InvokeAsync(() =>
        {
            _logger.LogDebug("RecordingHudWindow closing");
            StopCompleted?.Invoke();
            Close();
        });
    }

    private void SavedText_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var dir = Path.GetDirectoryName(_outputPath);
        if (dir is not null)
        {
            System.Diagnostics.Process.Start("explorer.exe", dir);
        }
    }
}
