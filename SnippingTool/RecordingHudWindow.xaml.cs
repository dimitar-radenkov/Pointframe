using System.IO;
using System.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SnippingTool.Models;
using SnippingTool.Services;

namespace SnippingTool;

public partial class RecordingHudWindow : Window
{
    private readonly IScreenRecordingService _svc;
    private readonly string _outputPath;
    private readonly ILogger<RecordingHudWindow> _logger;
    private readonly IOptions<RecordingOptions> _options;
    private CancellationTokenSource? _elapsedCts;
    private DateTime _startTime;

    private readonly Rect _regionRect;

    public event Action? StopCompleted;

    public RecordingHudWindow(IScreenRecordingService svc, string outputPath, ILogger<RecordingHudWindow> logger, Rect regionRect, IOptions<RecordingOptions> options)
    {
        _svc = svc;
        _outputPath = outputPath;
        _logger = logger;
        _options = options;
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
        var (left, top) = ComputePosition(_regionRect, ActualWidth, ActualHeight, SystemParameters.WorkArea, _options.Value.HudGapPixels);
        Left = left;
        Top = top;
        _logger.LogInformation("RecordingHudWindow rendered: ActualSize={W}x{H}, Position=({Left},{Top})",
            ActualWidth, ActualHeight, Left, Top);
    }

    // Extracted for unit testability.
    internal static (double Left, double Top) ComputePosition(
        Rect region, double hudWidth, double hudHeight, Rect workArea, int gapPixels = 8)
    {
        var left = Math.Max(workArea.Left, Math.Min(region.Left + (region.Width - hudWidth) / 2, workArea.Right - hudWidth));
        var top = Math.Min(region.Bottom + gapPixels, workArea.Bottom - hudHeight);
        return (left, top);
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

    protected override void OnClosed(EventArgs e)
    {
        _elapsedCts?.Cancel();
        base.OnClosed(e);
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("Stop button clicked");
        _elapsedCts?.Cancel();
        StopBtn.IsEnabled = false;

        _svc.Stop();

        var fileName = Path.GetFileName(_outputPath);
        SavedText.Text = $"Saved \u2192 {fileName}";
        SavedText.Visibility = Visibility.Visible;
        _logger.LogInformation("Recording saved to {Path}", _outputPath);

        _ = CloseAfterDelayAsync();
    }

    private async Task CloseAfterDelayAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(_options.Value.HudCloseDelaySeconds));
        await Dispatcher.InvokeAsync(() =>
        {
            if (!IsLoaded)
            {
                return;
            }

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
