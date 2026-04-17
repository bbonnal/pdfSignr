using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using pdfSignr.Models;
using pdfSignr.Services;

namespace pdfSignr.ViewModels;

// Page management: reordering, rotation, insertion, deletion, selection
public partial class MainViewModel
{
    public event Action? PageStructureChanged;

    [RelayCommand]
    private void MovePageUp(PageItem page)
    {
        if (page.IsSelected && SelectedPageCount > 1)
        {
            MoveSelectedPagesUp();
            return;
        }

        var idx = Pages.IndexOf(page);
        if (idx <= 0) return;
        Pages.Move(idx, idx - 1);
        RenumberPages();

        UndoRedo.Push(new UndoEntry(
            "Move page up",
            Undo: () => { Pages.Move(idx - 1, idx); RenumberPages(); },
            Redo: () => { Pages.Move(idx, idx - 1); RenumberPages(); }
        ));
    }

    [RelayCommand]
    private void MovePageDown(PageItem page)
    {
        if (page.IsSelected && SelectedPageCount > 1)
        {
            MoveSelectedPagesDown();
            return;
        }

        var idx = Pages.IndexOf(page);
        if (idx < 0 || idx >= Pages.Count - 1) return;
        Pages.Move(idx, idx + 1);
        RenumberPages();

        UndoRedo.Push(new UndoEntry(
            "Move page down",
            Undo: () => { Pages.Move(idx + 1, idx); RenumberPages(); },
            Redo: () => { Pages.Move(idx, idx + 1); RenumberPages(); }
        ));
    }

    private void MoveSelectedPagesUp()
    {
        var orderBefore = Pages.ToList();
        var selectedIndices = Pages
            .Select((p, i) => (p, i))
            .Where(x => x.p.IsSelected)
            .Select(x => x.i)
            .OrderBy(i => i)
            .ToList();

        int barrier = 0;
        bool moved = false;
        foreach (var idx in selectedIndices)
        {
            if (idx > barrier)
            {
                Pages.Move(idx, idx - 1);
                moved = true;
            }
            else
            {
                barrier = idx + 1;
            }
        }

        if (!moved) return;
        RenumberPages();

        var orderAfter = Pages.ToList();
        UndoRedo.Push(new UndoEntry(
            "Move selected pages up",
            Undo: () => ReorderPages(orderBefore),
            Redo: () => ReorderPages(orderAfter)
        ));
    }

    private void MoveSelectedPagesDown()
    {
        var orderBefore = Pages.ToList();
        var selectedIndices = Pages
            .Select((p, i) => (p, i))
            .Where(x => x.p.IsSelected)
            .Select(x => x.i)
            .OrderByDescending(i => i)
            .ToList();

        int barrier = Pages.Count - 1;
        bool moved = false;
        foreach (var idx in selectedIndices)
        {
            if (idx < barrier)
            {
                Pages.Move(idx, idx + 1);
                moved = true;
            }
            else
            {
                barrier = idx - 1;
            }
        }

        if (!moved) return;
        RenumberPages();

        var orderAfter = Pages.ToList();
        UndoRedo.Push(new UndoEntry(
            "Move selected pages down",
            Undo: () => ReorderPages(orderBefore),
            Redo: () => ReorderPages(orderAfter)
        ));
    }

    private void ReorderPages(IReadOnlyList<PageItem> order)
    {
        Pages.Clear();
        foreach (var p in order) Pages.Add(p);
        RenumberPages();
        PageStructureChanged?.Invoke();
    }

    public void MovePageByDrag(int fromIndex, int toIndex)
    {
        if (fromIndex == toIndex || fromIndex < 0 || toIndex < 0) return;
        Pages.Move(fromIndex, toIndex);
        RenumberPages();
        PageStructureChanged?.Invoke();

        UndoRedo.Push(new UndoEntry(
            "Reorder page",
            Undo: () => { Pages.Move(toIndex, fromIndex); RenumberPages(); PageStructureChanged?.Invoke(); },
            Redo: () => { Pages.Move(fromIndex, toIndex); RenumberPages(); PageStructureChanged?.Invoke(); }
        ));
    }

    public void MovePagesByDrag(IReadOnlyList<PageItem> pages, int targetIndex)
    {
        if (pages.Count == 0 || targetIndex < 0) return;

        var orderBefore = Pages.ToList();

        var indices = pages.Select(p => Pages.IndexOf(p)).Where(i => i >= 0).OrderByDescending(i => i).ToList();
        int countBefore = indices.Count(i => i < targetIndex);

        foreach (var idx in indices)
            Pages.RemoveAt(idx);

        int insertAt = Math.Min(targetIndex - countBefore, Pages.Count);

        var ordered = pages.Where(p => !Pages.Contains(p)).ToList();
        for (int i = 0; i < ordered.Count; i++)
            Pages.Insert(insertAt + i, ordered[i]);

        RenumberPages();
        PageStructureChanged?.Invoke();

        var orderAfter = Pages.ToList();
        UndoRedo.Push(new UndoEntry(
            $"Reorder {pages.Count} pages",
            Undo: () => ReorderPages(orderBefore),
            Redo: () => ReorderPages(orderAfter)
        ));
    }

    // --- Page rotation ---

    [RelayCommand]
    private void RotatePageCw(PageItem page)
    {
        if (page.IsSelected && SelectedPageCount > 1)
            RotateSelectedPages(90);
        else
            RotatePage(page, 90);
    }

    [RelayCommand]
    private void RotatePageCcw(PageItem page)
    {
        if (page.IsSelected && SelectedPageCount > 1)
            RotateSelectedPages(270);
        else
            RotatePage(page, 270);
    }

    private void RotatePage(PageItem page, int degrees)
    {
        int oldRotation = page.RotationDegrees;
        double oldW = page.WidthPt;
        double oldH = page.HeightPt;

        var annSnaps = page.Annotations.Select(a => (Ann: a, a.X, a.Y)).ToList();

        page.RotateAnnotations(degrees, oldW, oldH);
        page.RotationDegrees = (page.RotationDegrees + degrees) % 360;
        int newRotation = page.RotationDegrees;

        var annSnapsAfter = page.Annotations.Select(a => (Ann: a, a.X, a.Y)).ToList();

        PageRotated?.Invoke(page);

        UndoRedo.Push(new UndoEntry(
            "Rotate page",
            Undo: () =>
            {
                page.RotationDegrees = oldRotation;
                foreach (var (ann, x, y) in annSnaps) { ann.X = x; ann.Y = y; }
                PageRotated?.Invoke(page);
            },
            Redo: () =>
            {
                page.RotationDegrees = newRotation;
                foreach (var (ann, x, y) in annSnapsAfter) { ann.X = x; ann.Y = y; }
                PageRotated?.Invoke(page);
            }
        ));
    }

    private void RotateSelectedPages(int degrees)
    {
        var selected = SelectedPages.ToList();
        var snapshots = selected.Select(p => new
        {
            Page = p,
            OldRotation = p.RotationDegrees,
            OldW = p.WidthPt,
            OldH = p.HeightPt,
            AnnsBefore = p.Annotations.Select(a => (Ann: a, a.X, a.Y)).ToList()
        }).ToList();

        foreach (var snap in snapshots)
        {
            snap.Page.RotateAnnotations(degrees, snap.OldW, snap.OldH);
            snap.Page.RotationDegrees = (snap.Page.RotationDegrees + degrees) % 360;
        }

        var snapshotsAfter = snapshots.Select(s => new
        {
            s.Page,
            NewRotation = s.Page.RotationDegrees,
            AnnsAfter = s.Page.Annotations.Select(a => (Ann: a, a.X, a.Y)).ToList()
        }).ToList();

        foreach (var snap in snapshots)
            PageRotated?.Invoke(snap.Page);

        UndoRedo.Push(new UndoEntry(
            $"Rotate {selected.Count} pages",
            Undo: () =>
            {
                foreach (var snap in snapshots)
                {
                    snap.Page.RotationDegrees = snap.OldRotation;
                    foreach (var (ann, x, y) in snap.AnnsBefore) { ann.X = x; ann.Y = y; }
                    PageRotated?.Invoke(snap.Page);
                }
            },
            Redo: () =>
            {
                foreach (var snap in snapshotsAfter)
                {
                    snap.Page.RotationDegrees = snap.NewRotation;
                    foreach (var (ann, x, y) in snap.AnnsAfter) { ann.X = x; ann.Y = y; }
                    PageRotated?.Invoke(snap.Page);
                }
            }
        ));
    }

    public void RenumberPages()
    {
        for (int i = 0; i < Pages.Count; i++)
        {
            Pages[i].Index = i;
            Pages[i].DisplayNumber = i + 1;
            Pages[i].IsFirst = i == 0;
            foreach (var ann in Pages[i].Annotations)
                ann.PageIndex = i;
        }
        UpdatePageCount();
    }

    private void UpdatePageCount()
    {
        var name = PdfFilePath != null ? Path.GetFileName(PdfFilePath) : "Document";
        BaseStatus = $"{name} \u2014 {Pages.Count} page{(Pages.Count != 1 ? "s" : "")}";
    }

    // --- Page interleaving ---

    [RelayCommand]
    private async Task InsertPagesBefore(PageItem page)
    {
        await InsertPagesAt(Pages.IndexOf(page));
    }

    [RelayCommand]
    private async Task InsertPagesAfter(PageItem page)
    {
        await InsertPagesAt(Pages.IndexOf(page) + 1);
    }

    private async Task InsertPagesAt(int insertIndex)
    {
        var path = await _fileDialogs.PickOpenFileAsync("Insert PDF Pages", ["*.pdf"]);
        if (path == null) return;

        await InsertPagesFromFileAsync(path, insertIndex);
    }

    public async Task InsertPagesFromFileAsync(string path, int insertIndex)
    {
        try
        {
            if (!File.Exists(path))
            {
                BaseStatus = $"File not found: {Path.GetFileName(path)}";
                return;
            }

            var pdfBytes = await Task.Run(() => File.ReadAllBytes(path));
            var newPages = await RunWithPasswordRetryAsync(
                pw => CreatePageItems(pdfBytes, insertIndex, pw),
                Path.GetFileName(path), "Insert");
            if (newPages == null) return;

            for (int i = 0; i < newPages.Length; i++)
                Pages.Insert(insertIndex + i, newPages[i]);

            RenumberPages();

            if (PdfFilePath == null)
                PdfFilePath = path;

            var insertedPages = newPages.ToList();
            int count = insertedPages.Count;
            int insertAt = insertIndex;
            UndoRedo.Push(new UndoEntry(
                "Insert pages",
                Undo: () =>
                {
                    for (int i = count - 1; i >= 0; i--)
                        Pages.RemoveAt(insertAt + i);
                    if (Pages.Count == 0) { PdfFilePath = null; BaseStatus = DefaultStatus; }
                    else RenumberPages();
                },
                Redo: () =>
                {
                    for (int i = 0; i < count; i++)
                        Pages.Insert(insertAt + i, insertedPages[i]);
                    RenumberPages();
                }
            ));
        }
        catch (Exception ex)
        {
            BaseStatus = $"Failed to insert: {ex.Message}";
            _ = _fileDialogs.ShowErrorAsync("Failed to Insert", ex.Message);
        }
    }

    // --- Page selection ---

    [ObservableProperty] private int _selectedPageCount;
    private int _lastClickedPageIndex = -1;

    public IReadOnlyList<PageItem> SelectedPages => Pages.Where(p => p.IsSelected).ToList();
    public bool HasSelectedPages => SelectedPageCount > 0;

    public void SelectPage(PageItem page, bool ctrl, bool shift)
    {
        if (shift && _lastClickedPageIndex >= 0)
        {
            ClearPageSelection();
            int from = Math.Min(_lastClickedPageIndex, page.Index);
            int to = Math.Max(_lastClickedPageIndex, page.Index);
            for (int i = from; i <= to && i < Pages.Count; i++)
                Pages[i].IsSelected = true;
        }
        else if (ctrl)
        {
            page.IsSelected = !page.IsSelected;
            _lastClickedPageIndex = page.Index;
        }
        else
        {
            ClearPageSelection();
            page.IsSelected = true;
            _lastClickedPageIndex = page.Index;
        }
        UpdateSelectionState();
    }

    public void SelectAllPages()
    {
        foreach (var p in Pages) p.IsSelected = true;
        UpdateSelectionState();
    }

    public void ClearPageSelection()
    {
        foreach (var p in Pages) p.IsSelected = false;
        UpdateSelectionState();
    }

    public void UpdateSelectionState()
    {
        SelectedPageCount = Pages.Count(p => p.IsSelected);
        OnPropertyChanged(nameof(HasSelectedPages));
        SaveSelectedPagesCommand.NotifyCanExecuteChanged();
        DeleteSelectedPagesCommand.NotifyCanExecuteChanged();
        UpdateStatusText();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedPages))]
    private void DeleteSelectedPages()
    {
        var toRemove = SelectedPages.ToList();
        var snapshots = toRemove.Select(p => (Page: p, Index: Pages.IndexOf(p))).OrderByDescending(x => x.Index).ToList();

        if (SelectedAnnotation != null && toRemove.Any(p => p.Annotations.Contains(SelectedAnnotation)))
            SelectAnnotation(null);

        foreach (var (page, _) in snapshots)
            Pages.Remove(page);

        if (Pages.Count == 0) { PdfFilePath = null; BaseStatus = DefaultStatus; }
        else RenumberPages();
        PageStructureChanged?.Invoke();

        UndoRedo.Push(new UndoEntry(
            "Delete selected pages",
            Undo: () =>
            {
                foreach (var (page, idx) in snapshots.OrderBy(x => x.Index))
                    Pages.Insert(idx, page);
                RenumberPages();
                PageStructureChanged?.Invoke();
            },
            Redo: () =>
            {
                foreach (var (page, _) in snapshots)
                    Pages.Remove(page);
                if (Pages.Count == 0) { PdfFilePath = null; BaseStatus = DefaultStatus; }
                else RenumberPages();
                PageStructureChanged?.Invoke();
            }
        ));
    }
}
