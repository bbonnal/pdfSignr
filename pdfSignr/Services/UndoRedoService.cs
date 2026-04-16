using CommunityToolkit.Mvvm.ComponentModel;

namespace pdfSignr.Services;

public record UndoEntry(string Description, Action Undo, Action Redo);

public partial class UndoRedoService : ObservableObject
{
    private readonly Stack<UndoEntry> _undoStack = new();
    private readonly Stack<UndoEntry> _redoStack = new();
    private const int MaxDepth = 50;

    [ObservableProperty] private bool _canUndo;
    [ObservableProperty] private bool _canRedo;

    public void Push(UndoEntry entry)
    {
        _undoStack.Push(entry);
        _redoStack.Clear();
        if (_undoStack.Count > MaxDepth)
        {
            // Trim oldest entries by rebuilding the stack
            var items = _undoStack.ToArray();
            _undoStack.Clear();
            for (int i = MaxDepth - 1; i >= 0; i--)
                _undoStack.Push(items[i]);
        }
        UpdateState();
    }

    public void Undo()
    {
        if (_undoStack.Count == 0) return;
        var entry = _undoStack.Pop();
        entry.Undo();
        _redoStack.Push(entry);
        UpdateState();
    }

    public void Redo()
    {
        if (_redoStack.Count == 0) return;
        var entry = _redoStack.Pop();
        entry.Redo();
        _undoStack.Push(entry);
        UpdateState();
    }

    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        UpdateState();
    }

    private void UpdateState()
    {
        CanUndo = _undoStack.Count > 0;
        CanRedo = _redoStack.Count > 0;
    }
}
