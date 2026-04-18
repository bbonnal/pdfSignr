using CommunityToolkit.Mvvm.ComponentModel;

namespace pdfSignr.Services;

public partial class UndoRedoService : ObservableObject
{
    private readonly Stack<IUndoableCommand> _undoStack = new();
    private readonly Stack<IUndoableCommand> _redoStack = new();
    private readonly int _maxDepth;

    [ObservableProperty] private bool _canUndo;
    [ObservableProperty] private bool _canRedo;

    public UndoRedoService() : this(new Models.AppSettings().UndoMaxDepth) { }

    public UndoRedoService(ISettingsService settings)
        : this(settings.Current.UndoMaxDepth) { }

    public UndoRedoService(int maxDepth)
    {
        _maxDepth = Math.Max(1, maxDepth);
    }

    /// <summary>
    /// Records a command that has *already* been applied. Use this when the mutation
    /// happened inline and you just want the inverse on the stack.
    /// </summary>
    public void Push(IUndoableCommand cmd)
    {
        _undoStack.Push(cmd);
        _redoStack.Clear();
        Trim();
        UpdateState();
    }

    /// <summary>
    /// Executes the command and pushes it onto the undo stack. Preferred over
    /// manually mutating then calling <see cref="Push"/>, because it guarantees
    /// the stack and state stay in sync.
    /// </summary>
    public void Execute(IUndoableCommand cmd)
    {
        cmd.Execute();
        Push(cmd);
    }

    public void Undo()
    {
        if (_undoStack.Count == 0) return;
        var cmd = _undoStack.Pop();
        cmd.Undo();
        _redoStack.Push(cmd);
        UpdateState();
    }

    public void Redo()
    {
        if (_redoStack.Count == 0) return;
        var cmd = _redoStack.Pop();
        cmd.Execute();
        _undoStack.Push(cmd);
        UpdateState();
    }

    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        UpdateState();
    }

    private void Trim()
    {
        if (_undoStack.Count <= _maxDepth) return;
        var items = _undoStack.ToArray();
        _undoStack.Clear();
        for (int i = _maxDepth - 1; i >= 0; i--)
            _undoStack.Push(items[i]);
    }

    private void UpdateState()
    {
        CanUndo = _undoStack.Count > 0;
        CanRedo = _redoStack.Count > 0;
    }
}
