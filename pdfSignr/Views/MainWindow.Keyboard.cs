using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using pdfSignr.Models;
using pdfSignr.ViewModels;

namespace pdfSignr.Views;

// Keyboard shortcuts and page navigation
public partial class MainWindow
{
    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Don't intercept when editing text
        if (TextEditorOverlay.IsVisible && InlineTextBox.IsFocused)
        {
            if (e.Key == Key.Escape)
            {
                HideTextEditor();
                e.Handled = true;
            }
            return;
        }

        base.OnKeyDown(e);
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (ctrl)
        {
            if (e.Key == Key.Z && !shift)
            { if (ViewModel.UndoCommand.CanExecute(null)) ViewModel.UndoCommand.Execute(null); e.Handled = true; }
            else if (e.Key == Key.Y || (e.Key == Key.Z && shift))
            { if (ViewModel.RedoCommand.CanExecute(null)) ViewModel.RedoCommand.Execute(null); e.Handled = true; }
            else if (e.Key == Key.A)
            { ViewModel.SelectAllPages(); e.Handled = true; }
            else if (e.Key == Key.O)
            { ViewModel.OpenCommand.Execute(null); e.Handled = true; }
            else if (e.Key == Key.S)
            {
                // Save selected pages if selection exists, otherwise save all
                if (ViewModel.HasSelectedPages && ViewModel.SaveSelectedPagesCommand.CanExecute(null))
                    ViewModel.SaveSelectedPagesCommand.Execute(null);
                else if (ViewModel.SaveCommand.CanExecute(null))
                    ViewModel.SaveCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.D0 || e.Key == Key.NumPad0)
            { FitToWidth(); e.Handled = true; }
            else if (e.Key == Key.OemPlus || e.Key == Key.Add)
            { ApplyZoom(ZoomIn()); e.Handled = true; }
            else if (e.Key == Key.OemMinus || e.Key == Key.Subtract)
            { ApplyZoom(ZoomOut()); e.Handled = true; }
            else if (e.Key == Key.Up)
            { SelectAdjacentPage(-1, addToSelection: true); e.Handled = true; }
            else if (e.Key == Key.Down)
            { SelectAdjacentPage(1, addToSelection: true); e.Handled = true; }
        }
        else if (e.Key == Key.Delete || e.Key == Key.Back)
        {
            // Delete selected annotation first, then fall back to selected pages
            if (ViewModel.DeleteCommand.CanExecute(null))
            {
                HideTextEditor();
                ViewModel.DeleteCommand.Execute(null);
                e.Handled = true;
            }
            else if (ViewModel.HasSelectedPages)
            {
                ViewModel.DeleteSelectedPagesCommand.Execute(null);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Escape)
        {
            HideTextEditor();
            ViewModel.CurrentTool = ToolMode.Select;
            ViewModel.SelectAnnotation(null);
            if (ViewModel.HasSelectedPages)
                ViewModel.ClearPageSelection();
            e.Handled = true;
        }
        else if (e.Key == Key.R)
        {
            PageItem? target = null;
            if (ViewModel.HasSelectedPages)
                target = ViewModel.SelectedPages.First();
            else
            {
                var visible = GetVisiblePageIndices();
                if (visible.Count > 0)
                    target = ViewModel.Pages[visible.Min()];
            }
            if (target != null)
            {
                if (shift)
                    ViewModel.RotatePageCcwCommand.Execute(target);
                else
                    ViewModel.RotatePageCwCommand.Execute(target);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.M)
        {
            var target = GetSelectedOrVisiblePage();
            if (target != null)
            {
                if (shift)
                    ViewModel.MovePageDownCommand.Execute(target);
                else
                    ViewModel.MovePageUpCommand.Execute(target);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.S)
        {
            ViewModel.ToggleSignToolCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.A)
        {
            ViewModel.ToggleTextToolCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.G)
        {
            ViewModel.IsGridMode = !ViewModel.IsGridMode;
            ApplyGridMode(ViewModel.IsGridMode);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            SelectCenterPage();
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            ScrollByPages(-1);
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            ScrollByPages(1);
            e.Handled = true;
        }
        else if (e.Key == Key.D0 || e.Key == Key.NumPad0)
        { FitToWidth(); e.Handled = true; }
        else if (e.Key == Key.OemPlus || e.Key == Key.Add)
        { ApplyZoom(ZoomIn()); e.Handled = true; }
        else if (e.Key == Key.OemMinus || e.Key == Key.Subtract)
        { ApplyZoom(ZoomOut()); e.Handled = true; }
    }

    // ═══ Navigation helpers ═══

    private PageItem? GetSelectedOrVisiblePage()
    {
        if (ViewModel.HasSelectedPages)
            return ViewModel.SelectedPages.First();
        var visible = GetVisiblePageIndices();
        return visible.Count > 0 ? ViewModel.Pages[visible.Min()] : null;
    }

    private void SelectCenterPage()
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
            double dist = System.Math.Abs(center.Y - midY);
            if (dist < minDist) { minDist = dist; bestIndex = i; }
        }

        if (bestIndex >= 0)
        {
            ViewModel.ClearPageSelection();
            ViewModel.Pages[bestIndex].IsSelected = true;
            ViewModel.UpdateSelectionState();
        }
    }

    private void ScrollByPages(int direction)
    {
        // Scroll by roughly one page height
        double step = PdfScrollViewer.Viewport.Height * 0.85;
        var offset = PdfScrollViewer.Offset;
        PdfScrollViewer.Offset = new Vector(
            offset.X,
            System.Math.Max(0, offset.Y + direction * step));
    }

    private void SelectAdjacentPage(int direction, bool addToSelection)
    {
        if (ViewModel.Pages.Count == 0) return;

        // Find the "anchor" — the last selected page or the center page
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
            // Nothing selected — use center page
            SelectCenterPage();
            return;
        }

        int targetIndex = System.Math.Clamp(anchorIndex + direction, 0, ViewModel.Pages.Count - 1);
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
            // Page is above viewport — scroll up
            PdfScrollViewer.Offset = new Vector(
                PdfScrollViewer.Offset.X,
                PdfScrollViewer.Offset.Y + top.Value.Y - ScrollIntoViewMargin);
        }
        else if (bottom.Value.Y > viewH)
        {
            // Page is below viewport — scroll down
            PdfScrollViewer.Offset = new Vector(
                PdfScrollViewer.Offset.X,
                PdfScrollViewer.Offset.Y + bottom.Value.Y - viewH + ScrollIntoViewMargin);
        }
    }
}
