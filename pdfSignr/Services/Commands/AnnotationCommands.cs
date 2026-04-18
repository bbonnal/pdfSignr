using pdfSignr.Models;
using pdfSignr.ViewModels;

namespace pdfSignr.Services.Commands;

/// <summary>Adds an annotation to a page and selects it.</summary>
public sealed class AddAnnotationCommand(
    MainViewModel vm, PageItem page, Annotation annotation) : IUndoableCommand
{
    public string Description { get; } = annotation is TextAnnotation ? "Add text" : "Add signature";

    public void Execute()
    {
        page.Annotations.Add(annotation);
        vm.SelectAnnotation(annotation);
    }

    public void Undo()
    {
        page.Annotations.Remove(annotation);
        vm.SelectAnnotation(null);
    }
}

/// <summary>Removes an annotation from a page.</summary>
public sealed class DeleteAnnotationCommand(
    MainViewModel vm, PageItem page, Annotation annotation, int index) : IUndoableCommand
{
    public string Description => "Delete annotation";

    public void Execute()
    {
        page.Annotations.Remove(annotation);
        vm.SelectAnnotation(null);
    }

    public void Undo()
    {
        page.Annotations.Insert(index, annotation);
        vm.SelectAnnotation(annotation);
    }
}

/// <summary>Commits a move/resize/rotate manipulation from the view.</summary>
public sealed class ManipulateAnnotationCommand(
    Annotation ann,
    double oldX, double oldY, double oldW, double oldH, double oldRot,
    double newX, double newY, double newW, double newH, double newRot) : IUndoableCommand
{
    public string Description => "Move/resize annotation";

    public void Execute() => Apply(newX, newY, newW, newH, newRot);
    public void Undo() => Apply(oldX, oldY, oldW, oldH, oldRot);

    private void Apply(double x, double y, double w, double h, double rot)
    {
        ann.X = x; ann.Y = y; ann.WidthPt = w; ann.HeightPt = h; ann.Rotation = rot;
        if (ann is SvgAnnotation svg)
        {
            svg.Scale = svg.OriginalWidthPt > 0 ? w / svg.OriginalWidthPt : 1;
            svg.ReRender(PdfConstants.RenderDpi);
        }
    }
}

/// <summary>Edits a text annotation's text and/or font.</summary>
public sealed class EditTextAnnotationCommand(
    TextAnnotation ann,
    string oldText, string oldFont,
    string newText, string newFont) : IUndoableCommand
{
    public string Description => "Edit text";

    public void Execute() { ann.Text = newText; ann.FontFamily = newFont; }
    public void Undo() { ann.Text = oldText; ann.FontFamily = oldFont; }
}
