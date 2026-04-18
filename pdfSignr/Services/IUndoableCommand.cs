namespace pdfSignr.Services;

/// <summary>
/// A single reversible operation. Every mutation that should appear on the undo stack
/// must be expressed as one of these — either a typed subclass for structural operations,
/// or a <see cref="LambdaCommand"/> for one-off closures.
/// </summary>
public interface IUndoableCommand
{
    string Description { get; }
    void Execute();
    void Undo();
}

/// <summary>
/// General-purpose command built from closures. Convenient when the "do"/"undo" logic
/// is too small or too call-site-specific to justify a typed class.
/// For structural operations, prefer a typed subclass of <see cref="IUndoableCommand"/>.
/// </summary>
public sealed class LambdaCommand(string description, Action execute, Action undo) : IUndoableCommand
{
    public string Description { get; } = description;
    public void Execute() => execute();
    public void Undo() => undo();
}
