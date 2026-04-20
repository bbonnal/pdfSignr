using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using pdfSignr.Services;

namespace pdfSignr.Views;

// Keyboard dispatch (data-driven via IKeyBindingService) + navigation helpers
public partial class MainWindow
{
    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Text editing mode: only Escape escapes, everything else goes to the TextBox.
        if (TextEditorOverlay.IsVisible && InlineTextBox.IsFocused)
        {
            if (e.Key == Key.Escape)
            {
                HideTextEditor();
                e.Handled = true;
            }
            return;
        }

        // Escape during a page drag must abort the drag — before the generic Esc binding
        // clears selection, because the selection is what the user is dragging.
        if (e.Key == Key.Escape && IsPageDragActive)
        {
            CancelPageDrag();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
        // If a focused control (e.g. the page-number TextBox) already handled the key
        // for its own purposes — Ctrl+C/Ctrl+V on text, Enter to commit, etc. — don't
        // double-fire the app-level binding.
        if (e.Handled) return;

        var chord = new KeyChord(e.Key, e.KeyModifiers);
        if (_keyBindingService.TryDispatch(chord, ViewModel, this))
            e.Handled = true;
    }

    // ═══ Helpers called by keybinding handlers (internal so service handlers can reach them) ═══

    public void ZoomIn() => ApplyZoom(NextZoomIn());
    public void ZoomOut() => ApplyZoom(NextZoomOut());

    public ViewModels.PageItem? GetSelectedOrVisiblePage()
    {
        if (ViewModel.HasSelectedPages)
            return ViewModel.SelectedPages.First();
        var visible = GetVisiblePageIndices();
        return visible.Count > 0 ? ViewModel.Pages[visible.Min()] : null;
    }

    public void SelectCenterPage()
    {
        if (ViewModel.Pages.Count == 0) return;
        var center = new Point(
            PdfScrollViewer.Viewport.Width / 2,
            PdfScrollViewer.Viewport.Height / 2);

        var itemsControl = (ItemsControl)ZoomTransform.Child!;
        int bestIndex = -1;
        double minDist = double.MaxValue;

        for (int i = 0; i < ViewModel.Pages.Count; i++)
        {
            var container = itemsControl.ContainerFromIndex(i);
            if (container == null) continue;
            var top = container.TranslatePoint(new Point(0, 0), PdfScrollViewer);
            var bottom = container.TranslatePoint(
                new Point(container.Bounds.Width, container.Bounds.Height), PdfScrollViewer);
            if (top == null || bottom == null) continue;

            double midY = (top.Value.Y + bottom.Value.Y) / 2;
            double dist = Math.Abs(center.Y - midY);
            if (dist < minDist) { minDist = dist; bestIndex = i; }
        }

        if (bestIndex >= 0)
        {
            ViewModel.ClearPageSelection();
            ViewModel.Pages[bestIndex].IsSelected = true;
            ViewModel.UpdateSelectionState();
        }
    }

    public void ScrollByPages(int direction)
    {
        int pageCount = ViewModel.Pages.Count;
        if (pageCount == 0) return;

        // Snap to page boundaries so spacing between pages is included in the step.
        if (!_pageOffsetCacheValid || _pageTops.Length != pageCount)
            TryRebuildPageOffsetCache(pageCount);

        if (_pageOffsetCacheValid)
        {
            double scrollY = PdfScrollViewer.Offset.Y;
            // Current anchor = last page whose top is at or above the scroll position
            int anchor = 0;
            for (int i = 0; i < pageCount; i++)
            {
                if (_pageTops[i] <= scrollY + 0.5) anchor = i;
                else break;
            }

            int target = Math.Clamp(anchor + direction, 0, pageCount - 1);
            double targetY = target == 0 ? 0 : _pageTops[target];
            PdfScrollViewer.Offset = new Vector(
                PdfScrollViewer.Offset.X,
                Math.Max(0, targetY));
            return;
        }

        // Fallback if the cache couldn't be built (containers not yet realized)
        double step = PdfScrollViewer.Viewport.Height * 0.85;
        var offset = PdfScrollViewer.Offset;
        PdfScrollViewer.Offset = new Vector(
            offset.X,
            Math.Max(0, offset.Y + direction * step));
    }

    public void SelectAdjacentPage(int direction, bool addToSelection)
    {
        if (ViewModel.Pages.Count == 0) return;

        int anchorIndex;
        if (ViewModel.HasSelectedPages)
        {
            var selected = ViewModel.Pages
                .Select((p, i) => (p, i))
                .Where(x => x.p.IsSelected)
                .Select(x => x.i);
            anchorIndex = direction > 0 ? selected.Max() : selected.Min();
        }
        else
        {
            SelectCenterPage();
            return;
        }

        int targetIndex = Math.Clamp(anchorIndex + direction, 0, ViewModel.Pages.Count - 1);
        if (targetIndex == anchorIndex) return;

        if (addToSelection)
            ViewModel.Pages[targetIndex].IsSelected = true;
        else
        {
            ViewModel.ClearPageSelection();
            ViewModel.Pages[targetIndex].IsSelected = true;
        }
        ViewModel.UpdateSelectionState();
        ScrollPageIntoView(targetIndex);
    }

    private void ScrollPageIntoView(int pageIndex)
    {
        var itemsControl = (ItemsControl)ZoomTransform.Child!;
        var container = itemsControl.ContainerFromIndex(pageIndex);
        if (container == null) return;

        var top = container.TranslatePoint(new Point(0, 0), PdfScrollViewer);
        var bottom = container.TranslatePoint(
            new Point(0, container.Bounds.Height), PdfScrollViewer);
        if (top == null || bottom == null) return;

        double viewH = PdfScrollViewer.Viewport.Height;
        if (top.Value.Y < 0)
        {
            PdfScrollViewer.Offset = new Vector(
                PdfScrollViewer.Offset.X,
                PdfScrollViewer.Offset.Y + top.Value.Y - ScrollIntoViewMargin);
        }
        else if (bottom.Value.Y > viewH)
        {
            PdfScrollViewer.Offset = new Vector(
                PdfScrollViewer.Offset.X,
                PdfScrollViewer.Offset.Y + bottom.Value.Y - viewH + ScrollIntoViewMargin);
        }
    }
}
