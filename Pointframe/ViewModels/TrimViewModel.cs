using Pointframe.Services;

namespace Pointframe.ViewModels;

public partial class TrimViewModel : ObservableObject
{
    private const double MinTrimSeconds = 0.1;

    private readonly IVideoTrimService _trimService;
    private readonly ITelemetryService _telemetry;
    private readonly ILogger<TrimViewModel> _logger;
    private CancellationTokenSource? _trimCancellationSource;
    private bool _closeWhenTrimCanceled;

    public string InputPath { get; }

    public string FileName => Path.GetFileName(InputPath);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveTrimCommand))]
    private double _durationSeconds;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveTrimCommand))]
    [NotifyPropertyChangedFor(nameof(StartTimeText))]
    private double _startSeconds;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveTrimCommand))]
    [NotifyPropertyChangedFor(nameof(EndTimeText))]
    private double _endSeconds;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveTrimCommand))]
    private bool _isTrimming;

    [ObservableProperty]
    private string _statusText = string.Empty;

    public string StartTimeText => FormatTime(StartSeconds);
    public string EndTimeText => FormatTime(EndSeconds);

    public event Action? RequestClose;
    public event Action<string, TimeSpan>? TrimCompleted;

    public TrimViewModel(
        string inputPath,
        IVideoTrimService trimService,
        ITelemetryService telemetry,
        ILogger<TrimViewModel> logger)
    {
        InputPath = inputPath;
        _trimService = trimService;
        _telemetry = telemetry;
        _logger = logger;
    }

    public void SetMediaDuration(TimeSpan duration)
    {
        DurationSeconds = duration.TotalSeconds;
        StartSeconds = 0;
        EndSeconds = DurationSeconds;
    }

    partial void OnStartSecondsChanged(double value)
    {
        if (value > EndSeconds)
        {
            EndSeconds = value;
        }
    }

    partial void OnEndSecondsChanged(double value)
    {
        if (value < StartSeconds)
        {
            StartSeconds = value;
        }
    }

    private bool CanSaveTrim() =>
        !IsTrimming && DurationSeconds > 0 && EndSeconds - StartSeconds >= MinTrimSeconds;

    [RelayCommand(CanExecute = nameof(CanSaveTrim))]
    private async Task SaveTrim()
    {
        var outputPath = VideoTrimService.GetDefaultOutputPath(InputPath);
        var trimmedDuration = TimeSpan.FromSeconds(EndSeconds - StartSeconds);
        using var trimCancellationSource = new CancellationTokenSource();
        _trimCancellationSource = trimCancellationSource;
        _closeWhenTrimCanceled = false;

        IsTrimming = true;
        StatusText = "Trimming…";
        _telemetry.TrackEvent("video_trim_started");

        var canceled = false;
        var success = true;
        try
        {
            await _trimService.Trim(
                InputPath,
                outputPath,
                TimeSpan.FromSeconds(StartSeconds),
                TimeSpan.FromSeconds(EndSeconds),
                trimCancellationSource.Token).ConfigureAwait(true);

            TrimCompleted?.Invoke(outputPath, trimmedDuration);
            RequestClose?.Invoke();
        }
        catch (OperationCanceledException) when (trimCancellationSource.IsCancellationRequested)
        {
            success = false;
            canceled = true;
            StatusText = "Trim canceled.";
            if (_closeWhenTrimCanceled)
            {
                RequestClose?.Invoke();
            }
        }
        catch (Exception ex)
        {
            success = false;
            _logger.LogError(ex, "Trim failed for {Path}", InputPath);
            _telemetry.TrackException(ex, "video_trim");
            StatusText = "Trim failed. Please try again.";
        }
        finally
        {
            _trimCancellationSource = null;
            _closeWhenTrimCanceled = false;
            _telemetry.TrackEvent("video_trim_completed", new Dictionary<string, string>
            {
                ["success"] = success ? "true" : "false",
                ["canceled"] = canceled ? "true" : "false",
            });
            IsTrimming = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsTrimming)
        {
            if (_trimCancellationSource is null)
            {
                return;
            }

            _closeWhenTrimCanceled = true;
            StatusText = "Canceling…";
            _trimCancellationSource.Cancel();
            return;
        }

        RequestClose?.Invoke();
    }

    private static string FormatTime(double seconds) =>
        TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss\.f");
}
