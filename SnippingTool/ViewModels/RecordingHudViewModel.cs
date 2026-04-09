using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SnippingTool.Services;
using SnippingTool.Services.Messaging;

namespace SnippingTool.ViewModels;

public partial class RecordingHudViewModel : ObservableObject
{
    private readonly IScreenRecordingService _svc;
    private readonly IEventAggregator _eventAggregator;
    private readonly ILogger<RecordingHudViewModel> _logger;
    private RecordingAnnotationViewModel? _annotationViewModel;
    private Func<bool>? _toggleAnnotationInput;
    private CancellationTokenSource? _elapsedCts;
    private DateTime _startTime;
    private DateTime _pausedAt;
    private TimeSpan _totalPausedDuration;

    [ObservableProperty]
    private string _elapsedText = "00:00";

    [ObservableProperty]
    private string _pauseResumeLabel = "⏸ Pause";

    [ObservableProperty]
    private bool _canPauseResume = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnnotationPanelVisible))]
    private bool _canToggleAnnotation;

    [ObservableProperty]
    private string _annotationModeLabel = "Annotate";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnnotationPanelVisible))]
    private bool _canManageAnnotations;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnnotationPanelVisible))]
    [NotifyPropertyChangedFor(nameof(CurrentModeLabel))]
    private bool _isAnnotationInputArmed;

    public string OutputPath { get; }

    public bool IsAnnotationPanelVisible => CanManageAnnotations && IsAnnotationInputArmed;
    public string CurrentModeLabel => IsAnnotationInputArmed ? "Drawing" : "Interactive";

    public event Action? CloseRequested;

    public RecordingHudViewModel(
        IScreenRecordingService svc,
        string outputPath,
        IEventAggregator eventAggregator,
        ILogger<RecordingHudViewModel> logger)
    {
        _svc = svc;
        OutputPath = outputPath;
        _eventAggregator = eventAggregator;
        _logger = logger;
    }

    public void StartElapsedTimer()
    {
        _startTime = DateTime.UtcNow;
        _elapsedCts = new CancellationTokenSource();
        _ = RunElapsedTimer(_elapsedCts.Token);
    }

    public void AttachAnnotationSession(RecordingAnnotationViewModel annotationViewModel, Func<bool> toggleAnnotationInput)
    {
        _annotationViewModel = annotationViewModel;
        _toggleAnnotationInput = toggleAnnotationInput;
        CanToggleAnnotation = true;
        CanManageAnnotations = true;
        IsAnnotationInputArmed = false;
        AnnotationModeLabel = "Annotate";
    }

    public void CancelElapsedTimer() => _elapsedCts?.Cancel();

    private async Task RunElapsedTimer(CancellationToken ct)
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
        await Task.Run(() => _svc.Stop()).ConfigureAwait(true);
        _logger.LogInformation("Recording saved to {Path}", OutputPath);

        await _eventAggregator.Publish(new RecordingCompletedMessage(OutputPath, ElapsedText)).ConfigureAwait(true);
        CloseRequested?.Invoke();
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
    private void ToggleAnnotationInput()
    {
        if (_toggleAnnotationInput is null)
        {
            return;
        }

        var isInputArmed = _toggleAnnotationInput();
        IsAnnotationInputArmed = isInputArmed;
        AnnotationModeLabel = isInputArmed ? "Interact" : "Annotate";
        _logger.LogInformation("Recording annotation spike toggled: {IsInputArmed}", isInputArmed);
    }

    [RelayCommand]
    private void SelectTool(string? toolName)
    {
        if (_annotationViewModel is null
            || string.IsNullOrWhiteSpace(toolName)
            || !Enum.TryParse<AnnotationTool>(toolName, out var selectedTool))
        {
            return;
        }

        _annotationViewModel.SelectedTool = selectedTool;
        _logger.LogInformation("Recording annotation tool selected from HUD: {Tool}", selectedTool);
    }

    [RelayCommand]
    private void UndoAnnotations()
    {
        if (_annotationViewModel?.UndoCommand.CanExecute(null) != true)
        {
            return;
        }

        _annotationViewModel.UndoCommand.Execute(null);
    }

    [RelayCommand]
    private void ClearAnnotations()
    {
        if (_annotationViewModel?.ClearCommand.CanExecute(null) != true)
        {
            return;
        }

        _annotationViewModel.ClearCommand.Execute(null);
    }
}
