using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SnippingTool.Services;

namespace SnippingTool.ViewModels;

public partial class PreviewViewModel : AnnotationViewModel
{
    public PreviewViewModel(IAnnotationGeometryService geometry, ILogger<PreviewViewModel> logger)
        : base(geometry, logger) { }

    private readonly List<List<object>> _undoStack = [];
    private readonly List<List<object>> _redoStack = [];
    private List<object>? _currentGroup;

    [ObservableProperty]
    private double _canvasWidth;

    [ObservableProperty]
    private double _canvasHeight;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    private int _undoCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RedoCommand))]
    private int _redoCount;

    public void BeginGroup() => _currentGroup = [];

    public void CommitGroup()
    {
        if (_currentGroup is { Count: > 0 })
        {
            _undoStack.Add(_currentGroup);
            _redoStack.Clear();
            UndoCount = _undoStack.Count;
            RedoCount = 0;
        }

        _currentGroup = null;
    }

    public void TrackElement(object element) => _currentGroup?.Add(element);

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        var group = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        _redoStack.Add(group);
        UndoCount = _undoStack.Count;
        RedoCount = _redoStack.Count;
        _logger.LogDebug("Undo applied: undoStack={UndoCount}, redoStack={RedoCount}", UndoCount, RedoCount);
        UndoApplied?.Invoke(group);
    }

    private bool CanUndo() => _undoStack.Count > 0;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        var group = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        _undoStack.Add(group);
        UndoCount = _undoStack.Count;
        RedoCount = _redoStack.Count;
        _logger.LogDebug("Redo applied: undoStack={UndoCount}, redoStack={RedoCount}", UndoCount, RedoCount);
        RedoApplied?.Invoke(group);
    }

    private bool CanRedo() => _redoStack.Count > 0;

    public event Action<List<object>>? UndoApplied;
    public event Action<List<object>>? RedoApplied;

    public event Action? CopyRequested;
    public event Action? SaveRequested;
    public event Action? CloseRequested;

    [RelayCommand]
    private void Copy()
    {
        _logger.LogInformation("Copy command invoked");
        CopyRequested?.Invoke();
    }

    [RelayCommand]
    private void Save()
    {
        _logger.LogInformation("Save command invoked");
        SaveRequested?.Invoke();
    }

    [RelayCommand]
    private void Close()
    {
        _logger.LogInformation("Close command invoked");
        CloseRequested?.Invoke();
    }
}
