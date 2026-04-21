using pdfSignr.Models;
using pdfSignr.ViewModels;
using Xunit;

namespace pdfSignr.Tests;

public class MainViewModelClipboardTests
{
    private static TextAnnotation MakeText(double x, double y, double w = 60, double h = 20)
        // Empty FontFamily short-circuits FontSize computation so these tests don't need
        // an Avalonia text stack initialized.
        => new() { X = x, Y = y, WidthPt = w, HeightPt = h, Text = "t", FontFamily = "" };

    // ═══ Annotation copy/paste ═══

    [Fact]
    public void Paste_annotation_offsets_by_ten_points_on_same_page()
    {
        var vm = TestHarness.CreateViewModel();
        var page = TestHarness.AddPage(vm, 600, 800);
        var ann = MakeText(100, 200);
        page.Annotations.Add(ann);
        vm.SelectAnnotation(ann);

        vm.CopySelection();
        vm.PasteClipboard();

        Assert.Equal(2, page.Annotations.Count);
        var paste = page.Annotations[1];
        Assert.Equal(110, paste.X);
        Assert.Equal(210, paste.Y);
        Assert.Equal(page.Index, paste.PageIndex);
        Assert.NotSame(ann, paste);
    }

    [Fact]
    public void Paste_annotation_clamps_to_page_bounds()
    {
        var vm = TestHarness.CreateViewModel();
        var page = TestHarness.AddPage(vm, 600, 800);
        var ann = MakeText(595, 795, 20, 20); // already at right/bottom edge
        page.Annotations.Add(ann);
        vm.SelectAnnotation(ann);

        vm.CopySelection();
        vm.PasteClipboard();

        var paste = page.Annotations[1];
        Assert.Equal(580, paste.X); // 600 - 20
        Assert.Equal(780, paste.Y); // 800 - 20
    }

    [Fact]
    public void Paste_annotation_falls_back_to_first_page_when_owner_deleted()
    {
        var vm = TestHarness.CreateViewModel();
        var owner = TestHarness.AddPage(vm);
        var first = owner; // only page, so FirstOrDefault returns it
        var second = TestHarness.AddPage(vm);
        var ann = MakeText(50, 60);
        second.Annotations.Add(ann);
        vm.SelectAnnotation(ann);

        vm.CopySelection();
        vm.Pages.Remove(second);
        vm.PasteClipboard();

        Assert.Single(first.Annotations);
        Assert.Equal(first.Index, first.Annotations[0].PageIndex);
    }

    [Fact]
    public void Paste_annotation_is_undoable()
    {
        var vm = TestHarness.CreateViewModel();
        var page = TestHarness.AddPage(vm);
        page.Annotations.Add(MakeText(10, 10));
        vm.SelectAnnotation(page.Annotations[0]);

        vm.CopySelection();
        vm.PasteClipboard();
        Assert.Equal(2, page.Annotations.Count);

        vm.UndoRedo.Undo();
        Assert.Single(page.Annotations);

        vm.UndoRedo.Redo();
        Assert.Equal(2, page.Annotations.Count);
    }

    // ═══ Page copy/paste ═══

    [Fact]
    public void Paste_pages_inserts_after_last_selected()
    {
        var vm = TestHarness.CreateViewModel();
        var p0 = TestHarness.AddPage(vm);
        var p1 = TestHarness.AddPage(vm);
        var p2 = TestHarness.AddPage(vm);
        p0.IsSelected = true;
        p1.IsSelected = true;
        vm.UpdateSelectionState();

        vm.CopySelection();
        p0.IsSelected = false;
        p1.IsSelected = false;
        vm.UpdateSelectionState();
        p2.IsSelected = true;
        vm.UpdateSelectionState();

        vm.PasteClipboard();

        // Original p0 p1 copies should be inserted after p2 (current last selected)
        Assert.Equal(5, vm.Pages.Count);
        Assert.Same(p2, vm.Pages[2]);
        Assert.NotSame(p0, vm.Pages[3]);
        Assert.NotSame(p1, vm.Pages[4]);
    }

    [Fact]
    public void Paste_pages_without_selection_appends_after_last_copied()
    {
        var vm = TestHarness.CreateViewModel();
        var p0 = TestHarness.AddPage(vm);
        var p1 = TestHarness.AddPage(vm);
        var p2 = TestHarness.AddPage(vm);
        p0.IsSelected = true;
        vm.UpdateSelectionState();

        vm.CopySelection();
        p0.IsSelected = false;
        vm.UpdateSelectionState();

        vm.PasteClipboard();

        // Anchor at the copied page (p0, index 0), insert at index 1
        Assert.Equal(4, vm.Pages.Count);
        Assert.Same(p0, vm.Pages[0]);
        Assert.NotSame(p0, vm.Pages[1]);
        Assert.Same(p1, vm.Pages[2]);
        Assert.Same(p2, vm.Pages[3]);
    }

    [Fact]
    public void Copy_clears_the_other_kind()
    {
        var vm = TestHarness.CreateViewModel();
        var page = TestHarness.AddPage(vm);
        var ann = MakeText(10, 10);
        page.Annotations.Add(ann);
        vm.SelectAnnotation(ann);
        vm.CopySelection();

        // Now copy a page; the clipboard should forget the annotation
        vm.SelectAnnotation(null);
        page.IsSelected = true;
        vm.UpdateSelectionState();
        vm.CopySelection();

        vm.PasteClipboard();
        // Only the page was pasted; annotation on the cloned page is copied with the page, not separately
        Assert.Equal(2, vm.Pages.Count);
    }

    [Fact]
    public void Paste_pages_selects_inserted_copies()
    {
        var vm = TestHarness.CreateViewModel();
        var p0 = TestHarness.AddPage(vm);
        p0.IsSelected = true;
        vm.UpdateSelectionState();

        vm.CopySelection();
        vm.PasteClipboard();

        Assert.False(vm.Pages[0].IsSelected);
        Assert.True(vm.Pages[1].IsSelected);
    }
}
