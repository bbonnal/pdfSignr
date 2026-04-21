using pdfSignr.ViewModels;
using Xunit;

namespace pdfSignr.Tests;

public class MainViewModelPagesTests
{
    private static (MainViewModel vm, PageItem[] pages) BuildVm(int count)
    {
        var vm = TestHarness.CreateViewModel();
        var pages = Enumerable.Range(0, count)
            .Select(_ => TestHarness.AddPage(vm))
            .ToArray();
        return (vm, pages);
    }

    private static string OrderOf(MainViewModel vm, PageItem[] pages)
        => string.Concat(vm.Pages.Select(p => (char)('A' + Array.IndexOf(pages, p))));

    // ───── MovePagesByDrag ─────

    [Fact]
    public void MovePagesByDrag_moves_selection_to_end()
    {
        var (vm, pages) = BuildVm(5); // A B C D E
        vm.MovePagesByDrag(new[] { pages[1], pages[3] }, targetIndex: 5);
        Assert.Equal("ACEBD", OrderOf(vm, pages));
    }

    [Fact]
    public void MovePagesByDrag_moves_selection_to_start()
    {
        var (vm, pages) = BuildVm(5);
        vm.MovePagesByDrag(new[] { pages[2], pages[4] }, targetIndex: 0);
        Assert.Equal("CEABD", OrderOf(vm, pages));
    }

    [Fact]
    public void MovePagesByDrag_handles_target_before_all_selected()
    {
        var (vm, pages) = BuildVm(5);
        vm.MovePagesByDrag(new[] { pages[3], pages[4] }, targetIndex: 1);
        Assert.Equal("ADEBC", OrderOf(vm, pages));
    }

    [Fact]
    public void MovePagesByDrag_single_page_forward_accounts_for_removal_shift()
    {
        var (vm, pages) = BuildVm(5);
        vm.MovePagesByDrag(new[] { pages[1] }, targetIndex: 4);
        Assert.Equal("ACDBE", OrderOf(vm, pages));
    }

    [Fact]
    public void MovePagesByDrag_interleaves_correctly_when_target_sits_inside_selection()
    {
        var (vm, pages) = BuildVm(5);
        vm.MovePagesByDrag(new[] { pages[1], pages[3] }, targetIndex: 2);
        Assert.Equal("ABDCE", OrderOf(vm, pages));
    }

    [Fact]
    public void MovePagesByDrag_is_undoable_via_stack()
    {
        var (vm, pages) = BuildVm(5);

        vm.MovePagesByDrag(new[] { pages[0], pages[4] }, targetIndex: 3);
        var afterDrag = OrderOf(vm, pages);
        Assert.Equal("BCAED", afterDrag);
        Assert.True(vm.UndoRedo.CanUndo);

        vm.UndoRedo.Undo();
        Assert.Equal("ABCDE", OrderOf(vm, pages));

        vm.UndoRedo.Redo();
        Assert.Equal("BCAED", OrderOf(vm, pages));
    }

    [Fact]
    public void MovePagesByDrag_no_op_on_empty_selection()
    {
        var (vm, pages) = BuildVm(3);
        vm.MovePagesByDrag(Array.Empty<PageItem>(), targetIndex: 2);
        Assert.Equal("ABC", OrderOf(vm, pages));
        Assert.False(vm.UndoRedo.CanUndo);
    }

    [Fact]
    public void MovePagesByDrag_no_op_on_negative_target()
    {
        var (vm, pages) = BuildVm(3);
        vm.MovePagesByDrag(new[] { pages[0] }, targetIndex: -1);
        Assert.Equal("ABC", OrderOf(vm, pages));
        Assert.False(vm.UndoRedo.CanUndo);
    }

    [Fact]
    public void MovePagesByDrag_renumbers_and_updates_IsFirst()
    {
        var (vm, pages) = BuildVm(3);
        vm.MovePagesByDrag(new[] { pages[2] }, targetIndex: 0);
        Assert.Equal(0, vm.Pages[0].Index);
        Assert.Equal(1, vm.Pages[0].DisplayNumber);
        Assert.True(vm.Pages[0].IsFirst);
        Assert.False(vm.Pages[1].IsFirst);
    }
}
