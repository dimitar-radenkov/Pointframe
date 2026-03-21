using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SnippingTool.Services;
using SnippingTool.Services.Messaging;

namespace SnippingTool.ViewModels;

public partial class RecordingAnnotationViewModel : AnnotationViewModel
{
    [ObservableProperty]
    private bool _isInputArmed;

    [ObservableProperty]
    private bool _hasActiveAnnotations;

    public event Action? ClearRequested;

    public RecordingAnnotationViewModel(
        IAnnotationGeometryService geometry,
        ILogger<RecordingAnnotationViewModel> logger,
        IUserSettingsService settings,
        IEventAggregator eventAggregator)
        : base(geometry, logger, settings, eventAggregator)
    {
        SelectedTool = AnnotationTool.Pen;
    }

    public bool SetInputArmed(bool isInputArmed)
    {
        IsInputArmed = isInputArmed;
        return IsInputArmed;
    }

    public void MarkAnnotationCommitted()
    {
        HasActiveAnnotations = true;
    }

    public void SyncAnnotationState(bool hasActiveAnnotations, int numberCounter)
    {
        HasActiveAnnotations = hasActiveAnnotations;
        ResetNumberCounter(numberCounter);
    }

    [RelayCommand]
    private void Clear()
    {
        ClearHistoryState();
        SyncAnnotationState(false, 0);
        ClearRequested?.Invoke();
        _logger.LogInformation("Recording annotations cleared");
    }
}
