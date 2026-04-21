using pdfSignr.Models;
using pdfSignr.Services.Commands;

namespace pdfSignr.ViewModels;

// Ctrl-C / Ctrl-V for annotations and pages. Both routes go through the undo stack.
public partial class MainViewModel
{
    private const double PasteOffsetPt = 10;

    /// <summary>
    /// Ctrl-C. Annotation selection wins over page selection — matches every desktop
    /// editor where the more-specific selection is what the user means.
    /// </summary>
    public void CopySelection()
    {
        if (SelectedAnnotation != null)
        {
            var owner = FindAnnotationOwner(SelectedAnnotation);
            if (owner != null) _clipboard.SetAnnotation(SelectedAnnotation, owner);
            return;
        }

        if (HasSelectedPages)
            _clipboard.SetPages(SelectedPages);
    }

    /// <summary>Ctrl-V. Pastes whichever kind was last copied.</summary>
    public void PasteClipboard()
    {
        if (_clipboard.HasAnnotation) PasteAnnotation();
        else if (_clipboard.HasPages) PastePages();
    }

    private void PasteAnnotation()
    {
        var original = _clipboard.Annotation;
        if (original == null) return;

        // Prefer the original's owner page; if it's been deleted, fall back to the first page.
        var targetPage = _clipboard.AnnotationOwner != null && Pages.Contains(_clipboard.AnnotationOwner)
            ? _clipboard.AnnotationOwner
            : Pages.FirstOrDefault();
        if (targetPage == null) return;

        var clone = original.Clone();
        // Offset slightly so the paste is visible instead of sitting on top of the original,
        // clamped to the page so rotated pastes don't fall off.
        (clone.X, clone.Y) = PagePlacement.ClampToPage(
            clone.X + PasteOffsetPt, clone.Y + PasteOffsetPt,
            clone.WidthPt, clone.HeightPt, targetPage.WidthPt, targetPage.HeightPt);
        clone.PageIndex = targetPage.Index;

        UndoRedo.Execute(new AddAnnotationCommand(this, targetPage, clone));
    }

    private void PastePages()
    {
        var source = _clipboard.Pages;
        if (source.Count == 0) return;

        // Insert after the last currently-selected page if any, else after the last copied
        // page if it's still in the document, else at the end.
        int insertAt;
        if (HasSelectedPages)
            insertAt = Pages.IndexOf(SelectedPages.Last()) + 1;
        else
        {
            int anchor = source.Select(p => Pages.IndexOf(p)).Where(i => i >= 0).DefaultIfEmpty(-1).Max();
            insertAt = anchor >= 0 ? anchor + 1 : Pages.Count;
        }
        insertAt = Math.Clamp(insertAt, 0, Pages.Count);

        var clones = source.Select(p => p.Clone()).ToList();
        var priorSelection = SelectedPages.ToList();

        UndoRedo.Execute(new DuplicatePagesCommand(
            Pages, clones, insertAt, priorSelection,
            () =>
            {
                RenumberPages();
                UpdateSelectionState();
                PageStructureChanged?.Invoke();
            }));
    }

    private PageItem? FindAnnotationOwner(Annotation ann)
    {
        foreach (var page in Pages)
            if (page.Annotations.Contains(ann)) return page;
        return null;
    }
}
