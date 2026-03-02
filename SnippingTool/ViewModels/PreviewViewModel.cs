using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SnippingTool.ViewModels;

public partial class PreviewViewModel : AnnotationViewModel
{
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
        RedoApplied?.Invoke(group);
    }

    private bool CanRedo() => _redoStack.Count > 0;

    public event Action<List<object>>? UndoApplied;
    public event Action<List<object>>? RedoApplied;

    public event Action? CopyRequested;
    public event Action? SaveRequested;
    public event Action? CloseRequested;

    [RelayCommand]
    private void Copy() => CopyRequested?.Invoke();

    [RelayCommand]
    private void Save() => SaveRequested?.Invoke();

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();
}
