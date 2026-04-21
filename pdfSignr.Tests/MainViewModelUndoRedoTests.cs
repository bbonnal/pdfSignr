using pdfSignr.Models;
using pdfSignr.ViewModels;
using Xunit;

namespace pdfSignr.Tests;

public class MainViewModelUndoRedoTests
{
    private static TextAnnotation MakeText(double x, double y)
        // Empty FontFamily short-circuits FontSize computation so these tests don't need
        // an Avalonia text stack initialized.
        => new() { X = x, Y = y, WidthPt = 60, HeightPt = 20, Text = "t", FontFamily = "" };

    // ───── Rotate ─────

    [Fact]
    public void Rotate_cw_then_undo_restores_rotation_and_annotation_positions()
    {
        var vm = TestHarness.CreateViewModel();
        var page = TestHarness.AddPage(vm, 600, 800);
        var ann = MakeText(100, 200);
        page.Annotations.Add(ann);

        vm.RotatePageCwCommand.Execute(page);
        Assert.Equal(90, page.RotationDegrees);
        // 90° CW: (x, y) → (oldH - y - h, x) = (800 - 200 - 20, 100) = (580, 100)
        Assert.Equal(580, ann.X);
        Assert.Equal(100, ann.Y);

        vm.UndoRedo.Undo();
        Assert.Equal(0, page.RotationDegrees);
        Assert.Equal(100, ann.X);
        Assert.Equal(200, ann.Y);
    }

    [Fact]
    public void Rotate_cw_four_times_through_stack_returns_to_identity()
    {
        var vm = TestHarness.CreateViewModel();
        var page = TestHarness.AddPage(vm, 600, 800);
        var ann = MakeText(100, 200);
        page.Annotations.Add(ann);

        for (int i = 0; i < 4; i++)
            vm.RotatePageCwCommand.Execute(page);

        Assert.Equal(0, page.RotationDegrees);
        Assert.Equal(100, ann.X);
        Assert.Equal(200, ann.Y);
    }

    [Fact]
    public void Rotate_records_single_undo_entry()
    {
        var vm = TestHarness.CreateViewModel();
        var page = TestHarness.AddPage(vm, 600, 800);

        vm.RotatePageCwCommand.Execute(page);
        Assert.True(vm.UndoRedo.CanUndo);
        vm.UndoRedo.Undo();
        Assert.False(vm.UndoRedo.CanUndo);
    }

    // ───── Delete ─────

    [Fact]
    public void Delete_then_undo_restores_annotation()
    {
        var vm = TestHarness.CreateViewModel();
        var page = TestHarness.AddPage(vm);
        var ann = MakeText(50, 50);
        page.Annotations.Add(ann);
        vm.SelectAnnotation(ann);

        vm.DeleteCommand.Execute(null);
        Assert.Empty(page.Annotations);

        vm.UndoRedo.Undo();
        Assert.Contains(ann, page.Annotations);
        Assert.Same(ann, vm.SelectedAnnotation);
    }

    [Fact]
    public void Delete_then_redo_re_removes_annotation()
    {
        var vm = TestHarness.CreateViewModel();
        var page = TestHarness.AddPage(vm);
        var ann = MakeText(50, 50);
        page.Annotations.Add(ann);
        vm.SelectAnnotation(ann);

        vm.DeleteCommand.Execute(null);
        vm.UndoRedo.Undo();
        vm.UndoRedo.Redo();
        Assert.Empty(page.Annotations);
    }

    [Fact]
    public void DeleteSelectedPages_removes_and_restores_pages()
    {
        var vm = TestHarness.CreateViewModel();
        var p0 = TestHarness.AddPage(vm);
        var p1 = TestHarness.AddPage(vm);
        var p2 = TestHarness.AddPage(vm);
        p0.IsSelected = true;
        p2.IsSelected = true;
        vm.UpdateSelectionState();

        vm.DeleteSelectedPagesCommand.Execute(null);
        Assert.Equal(new[] { p1 }, vm.Pages);

        vm.UndoRedo.Undo();
        Assert.Equal(new[] { p0, p1, p2 }, vm.Pages);
    }

    // ───── Sequences ─────

    [Fact]
    public void Rotate_then_delete_then_undo_twice_returns_to_original()
    {
        var vm = TestHarness.CreateViewModel();
        var p0 = TestHarness.AddPage(vm, 600, 800);
        var p1 = TestHarness.AddPage(vm, 600, 800);
        var ann = MakeText(100, 200);
        p0.Annotations.Add(ann);

        vm.RotatePageCwCommand.Execute(p0);
        p1.IsSelected = true;
        vm.UpdateSelectionState();
        vm.DeleteSelectedPagesCommand.Execute(null);

        Assert.Equal(new[] { p0 }, vm.Pages);
        Assert.Equal(90, p0.RotationDegrees);

        vm.UndoRedo.Undo();
        Assert.Equal(new[] { p0, p1 }, vm.Pages);
        Assert.Equal(90, p0.RotationDegrees);

        vm.UndoRedo.Undo();
        Assert.Equal(0, p0.RotationDegrees);
        Assert.Equal(100, ann.X);
        Assert.Equal(200, ann.Y);
    }

    [Fact]
    public void New_action_after_undo_clears_redo_stack()
    {
        var vm = TestHarness.CreateViewModel();
        var p0 = TestHarness.AddPage(vm);
        var p1 = TestHarness.AddPage(vm);

        vm.MovePageByDrag(0, 1);
        Assert.True(vm.UndoRedo.CanUndo);

        vm.UndoRedo.Undo();
        Assert.True(vm.UndoRedo.CanRedo);

        p0.IsSelected = true;
        vm.UpdateSelectionState();
        vm.DeleteSelectedPagesCommand.Execute(null);

        Assert.False(vm.UndoRedo.CanRedo);
    }

    [Fact]
    public void MovePageByDrag_is_undoable()
    {
        var vm = TestHarness.CreateViewModel();
        var p0 = TestHarness.AddPage(vm);
        var p1 = TestHarness.AddPage(vm);
        var p2 = TestHarness.AddPage(vm);

        vm.MovePageByDrag(0, 2);
        Assert.Equal(new[] { p1, p2, p0 }, vm.Pages);

        vm.UndoRedo.Undo();
        Assert.Equal(new[] { p0, p1, p2 }, vm.Pages);
    }
}
