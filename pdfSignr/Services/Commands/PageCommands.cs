using System.Collections.ObjectModel;
using pdfSignr.ViewModels;

namespace pdfSignr.Services.Commands;

/// <summary>Moves a single page within the collection by one position.</summary>
public sealed class MovePageCommand(
    ObservableCollection<PageItem> pages, int fromIndex, int toIndex,
    Action afterApply) : IUndoableCommand
{
    public string Description => fromIndex < toIndex ? "Move page down" : "Move page up";

    public void Execute() { pages.Move(fromIndex, toIndex); afterApply(); }
    public void Undo() { pages.Move(toIndex, fromIndex); afterApply(); }
}

/// <summary>Restores a specific ordering of the pages collection (both undo and redo).</summary>
public sealed class ReorderPagesCommand(
    string description,
    IReadOnlyList<PageItem> before,
    IReadOnlyList<PageItem> after,
    Action<IReadOnlyList<PageItem>> applyOrder) : IUndoableCommand
{
    public string Description { get; } = description;

    public void Execute() => applyOrder(after);
    public void Undo() => applyOrder(before);
}

/// <summary>Rotates one or more pages and their annotations.</summary>
public sealed class RotatePagesCommand : IUndoableCommand
{
    public string Description { get; }

    private readonly IReadOnlyList<PageState> _states;
    private readonly Action<PageItem> _onRotated;

    public RotatePagesCommand(string description, IReadOnlyList<PageState> states, Action<PageItem> onRotated)
    {
        Description = description;
        _states = states;
        _onRotated = onRotated;
    }

    public void Execute()
    {
        foreach (var s in _states)
        {
            s.Page.RotationDegrees = s.NewRotation;
            foreach (var (ann, x, y) in s.AnnsAfter) { ann.X = x; ann.Y = y; }
            _onRotated(s.Page);
        }
    }

    public void Undo()
    {
        foreach (var s in _states)
        {
            s.Page.RotationDegrees = s.OldRotation;
            foreach (var (ann, x, y) in s.AnnsBefore) { ann.X = x; ann.Y = y; }
            _onRotated(s.Page);
        }
    }

    public record PageState(
        PageItem Page,
        int OldRotation, int NewRotation,
        IReadOnlyList<(Models.Annotation Ann, double X, double Y)> AnnsBefore,
        IReadOnlyList<(Models.Annotation Ann, double X, double Y)> AnnsAfter);
}

/// <summary>Removes a set of pages from the collection and restores them on undo.</summary>
public sealed class DeletePagesCommand : IUndoableCommand
{
    public string Description => "Delete selected pages";

    private readonly ObservableCollection<PageItem> _pages;
    private readonly IReadOnlyList<(PageItem Page, int Index)> _removedOrderedByIndexDesc;
    private readonly Action _afterApply;

    public DeletePagesCommand(
        ObservableCollection<PageItem> pages,
        IReadOnlyList<(PageItem Page, int Index)> removedOrderedByIndexDesc,
        Action afterApply)
    {
        _pages = pages;
        _removedOrderedByIndexDesc = removedOrderedByIndexDesc;
        _afterApply = afterApply;
    }

    public void Execute()
    {
        foreach (var (page, _) in _removedOrderedByIndexDesc)
            _pages.Remove(page);
        _afterApply();
    }

    public void Undo()
    {
        foreach (var (page, idx) in _removedOrderedByIndexDesc.OrderBy(x => x.Index))
            _pages.Insert(idx, page);
        _afterApply();
    }
}

/// <summary>
/// Inserts a contiguous range of pages at a specific index and restores the page-selection
/// to the inserted pages on redo, to the prior selection on undo. Used by Ctrl-V on pages.
/// </summary>
public sealed class DuplicatePagesCommand : IUndoableCommand
{
    public string Description => _inserted.Count == 1 ? "Paste page" : $"Paste {_inserted.Count} pages";

    private readonly ObservableCollection<PageItem> _pages;
    private readonly IReadOnlyList<PageItem> _inserted;
    private readonly int _insertAt;
    private readonly IReadOnlyList<PageItem> _priorSelection;
    private readonly Action _afterApply;

    public DuplicatePagesCommand(
        ObservableCollection<PageItem> pages,
        IReadOnlyList<PageItem> inserted,
        int insertAt,
        IReadOnlyList<PageItem> priorSelection,
        Action afterApply)
    {
        _pages = pages;
        _inserted = inserted;
        _insertAt = insertAt;
        _priorSelection = priorSelection;
        _afterApply = afterApply;
    }

    public void Execute()
    {
        for (int i = 0; i < _inserted.Count; i++)
            _pages.Insert(_insertAt + i, _inserted[i]);
        foreach (var p in _pages) p.IsSelected = false;
        foreach (var p in _inserted) p.IsSelected = true;
        _afterApply();
    }

    public void Undo()
    {
        for (int i = _inserted.Count - 1; i >= 0; i--)
            _pages.RemoveAt(_insertAt + i);
        foreach (var p in _pages) p.IsSelected = false;
        foreach (var p in _priorSelection) p.IsSelected = true;
        _afterApply();
    }
}

/// <summary>Inserts a contiguous range of pages at a specific index.</summary>
public sealed class InsertPagesCommand : IUndoableCommand
{
    public string Description => "Insert pages";

    private readonly ObservableCollection<PageItem> _pages;
    private readonly IReadOnlyList<PageItem> _inserted;
    private readonly int _insertAt;
    private readonly Action _afterApply;

    public InsertPagesCommand(
        ObservableCollection<PageItem> pages,
        IReadOnlyList<PageItem> inserted,
        int insertAt,
        Action afterApply)
    {
        _pages = pages;
        _inserted = inserted;
        _insertAt = insertAt;
        _afterApply = afterApply;
    }

    public void Execute()
    {
        for (int i = 0; i < _inserted.Count; i++)
            _pages.Insert(_insertAt + i, _inserted[i]);
        _afterApply();
    }

    public void Undo()
    {
        for (int i = _inserted.Count - 1; i >= 0; i--)
            _pages.RemoveAt(_insertAt + i);
        _afterApply();
    }
}
