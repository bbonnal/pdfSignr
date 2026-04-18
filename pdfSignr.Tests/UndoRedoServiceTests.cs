using pdfSignr.Services;
using Xunit;

namespace pdfSignr.Tests;

public class UndoRedoServiceTests
{
    private sealed class Counter : IUndoableCommand
    {
        public int Value;
        public string Description => "counter";
        public void Execute() => Value++;
        public void Undo() => Value--;
    }

    [Fact]
    public void Execute_then_Undo_then_Redo_keeps_state_consistent()
    {
        var svc = new UndoRedoService();
        var c = new Counter();

        svc.Execute(c);
        Assert.Equal(1, c.Value);
        Assert.True(svc.CanUndo);
        Assert.False(svc.CanRedo);

        svc.Undo();
        Assert.Equal(0, c.Value);
        Assert.False(svc.CanUndo);
        Assert.True(svc.CanRedo);

        svc.Redo();
        Assert.Equal(1, c.Value);
        Assert.True(svc.CanUndo);
        Assert.False(svc.CanRedo);
    }

    [Fact]
    public void Push_records_command_without_executing_it()
    {
        var svc = new UndoRedoService();
        var c = new Counter();

        svc.Push(c);
        Assert.Equal(0, c.Value);
        Assert.True(svc.CanUndo);

        svc.Undo();
        Assert.Equal(-1, c.Value);

        svc.Redo();
        Assert.Equal(0, c.Value);
    }

    [Fact]
    public void New_push_clears_redo_stack()
    {
        var svc = new UndoRedoService();

        svc.Execute(new Counter());
        svc.Undo();
        Assert.True(svc.CanRedo);

        svc.Execute(new Counter());
        Assert.False(svc.CanRedo);
    }

    [Fact]
    public void Max_depth_trims_oldest_entries_first()
    {
        var svc = new UndoRedoService(maxDepth: 3);

        for (int i = 0; i < 5; i++)
            svc.Execute(new Counter());

        // Should hold 3, so undoing 3 times leaves stack empty
        svc.Undo(); svc.Undo(); svc.Undo();
        Assert.False(svc.CanUndo);
    }

    [Fact]
    public void Clear_empties_both_stacks()
    {
        var svc = new UndoRedoService();
        svc.Execute(new Counter());
        svc.Execute(new Counter());
        svc.Undo();

        svc.Clear();
        Assert.False(svc.CanUndo);
        Assert.False(svc.CanRedo);
    }
}
