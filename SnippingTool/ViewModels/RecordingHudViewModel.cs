using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SnippingTool.Services;

namespace SnippingTool.ViewModels;

public partial class RecordingHudViewModel : ObservableObject
{
    private readonly IScreenRecordingService _svc;
    private readonly IUserSettingsService _settings;
    private readonly IProcessService _process;
    private readonly ILogger<RecordingHudViewModel> _logger;
    private CancellationTokenSource? _elapsedCts;
    private DateTime _startTime;
    private DateTime _pausedAt;
    private TimeSpan _totalPausedDuration;

    [ObservableProperty]
    private string _elapsedText = "00:00";

    [ObservableProperty]
    private string _savedFileName = string.Empty;

    [ObservableProperty]
    private bool _isStopped;

    [ObservableProperty]
    private string _pauseResumeLabel = "⏸ Pause";

    [ObservableProperty]
    private bool _canPauseResume = true;

    public string OutputPath { get; }

    public event Action? StopCompleted;

    public RecordingHudViewModel(
        IScreenRecordingService svc,
        string outputPath,
        IUserSettingsService settings,
        IProcessService process,
        ILogger<RecordingHudViewModel> logger)
    {
        _svc = svc;
        OutputPath = outputPath;
        _settings = settings;
        _process = process;
        _logger = logger;
    }

    public void StartElapsedTimer()
    {
        _startTime = DateTime.UtcNow;
        _elapsedCts = new CancellationTokenSource();
        _ = RunElapsedTimerAsync(_elapsedCts.Token);
    }

    public void CancelElapsedTimer() => _elapsedCts?.Cancel();

    private async Task RunElapsedTimerAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (_svc.IsPaused)
                {
                    continue;
                }

                var elapsed = DateTime.UtcNow - _startTime - _totalPausedDuration;
                ElapsedText = elapsed.ToString(@"mm\:ss");
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    [RelayCommand]
    private async Task Stop()
    {
        _logger.LogInformation("Stop command executed");
        CanPauseResume = false;
        CancelElapsedTimer();
        _svc.Stop();
        SavedFileName = $"Saved \u2192 {Path.GetFileName(OutputPath)}";
        IsStopped = true;
        _logger.LogInformation("Recording saved to {Path}", OutputPath);
        await Task.Delay(TimeSpan.FromSeconds(_settings.Current.HudCloseDelaySeconds));
        StopCompleted?.Invoke();
    }

    [RelayCommand]
    private void PauseResume()
    {
        if (_svc.IsPaused)
        {
            _totalPausedDuration += DateTime.UtcNow - _pausedAt;
            _svc.Resume();
            PauseResumeLabel = "⏸ Pause";
            _logger.LogInformation("Recording resumed from HUD");
        }
        else
        {
            _pausedAt = DateTime.UtcNow;
            _svc.Pause();
            PauseResumeLabel = "▶ Resume";
            _logger.LogInformation("Recording paused from HUD");
        }
    }

    [RelayCommand]
    private void OpenOutputFolder()
    {
        var dir = Path.GetDirectoryName(OutputPath);
        if (dir is not null)
        {
            _process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", dir));
        }
    }
}
