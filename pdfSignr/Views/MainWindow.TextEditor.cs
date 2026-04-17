using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using pdfSignr.Models;
using pdfSignr.ViewModels;

namespace pdfSignr.Views;

// Inline text annotation editor overlay
public partial class MainWindow
{
    private TextAnnotation? _editingText;
    private string? _textBeforeEdit;
    private string? _fontBeforeEdit;

    internal void UpdateTextEditor()
    {
        if (ViewModel.SelectedAnnotation is TextAnnotation text)
            ShowTextEditor(text);
        else
            HideTextEditor();
    }

    private void ShowTextEditor(TextAnnotation text)
    {
        if (_editingText == text) return;

        // Detach old
        if (_editingText != null)
        {
            _editingText.PropertyChanged -= OnEditingAnnotationMoved;
            InlineTextBox.TextChanged -= OnInlineTextChanged;
            InlineFontCombo.SelectionChanged -= OnInlineFontChanged;
        }

        _editingText = text;
        _textBeforeEdit = text.Text;
        _fontBeforeEdit = text.FontFamily;

        // Set values
        InlineTextBox.Text = text.Text;
        InlineFontCombo.SelectedItem = text.FontFamily;

        // Attach live bindings
        InlineTextBox.TextChanged += OnInlineTextChanged;
        InlineFontCombo.SelectionChanged += OnInlineFontChanged;
        _editingText.PropertyChanged += OnEditingAnnotationMoved;

        // Position and show
        PositionEditorOverlay();
        TextEditorOverlay.IsVisible = true;
        InlineTextBox.Focus();
        InlineTextBox.SelectAll();
    }

    internal void HideTextEditor()
    {
        if (_editingText != null)
        {
            // Push undo entry if text or font changed
            var ann = _editingText;
            var oldText = _textBeforeEdit;
            var oldFont = _fontBeforeEdit;
            var newText = ann.Text;
            var newFont = ann.FontFamily;

            if (oldText != newText || oldFont != newFont)
            {
                ViewModel.UndoRedo.Push(new Services.UndoEntry(
                    "Edit text",
                    Undo: () => { ann.Text = oldText!; ann.FontFamily = oldFont!; },
                    Redo: () => { ann.Text = newText; ann.FontFamily = newFont; }
                ));
            }

            _editingText.PropertyChanged -= OnEditingAnnotationMoved;
            InlineTextBox.TextChanged -= OnInlineTextChanged;
            InlineFontCombo.SelectionChanged -= OnInlineFontChanged;
            _editingText = null;
        }
        TextEditorOverlay.IsVisible = false;
    }

    private void OnInlineTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_editingText != null && InlineTextBox.Text != null)
            _editingText.Text = InlineTextBox.Text;
    }

    private void OnInlineFontChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_editingText != null && InlineFontCombo.SelectedItem is string font)
            _editingText.FontFamily = font;
    }

    private void OnEditingAnnotationMoved(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "X" or "Y" or "HeightPt")
            PositionEditorOverlay();
    }

    private void PositionEditorOverlay()
    {
        if (_editingText == null) return;

        double annScreenX = _editingText.X * PdfConstants.DpiScale;
        double annScreenBottom = (_editingText.Y + _editingText.HeightPt) * PdfConstants.DpiScale + 8;

        // Find the PageCanvas hosting this annotation
        var canvas = FindCanvasForAnnotation(_editingText);
        if (canvas == null) return;

        var transform = canvas.TransformToVisual(this);
        if (transform == null) return;

        var pos = transform.Value.Transform(new Point(annScreenX, annScreenBottom));

        // Clamp within window bounds
        pos = new Point(
            System.Math.Clamp(pos.X, 50, System.Math.Max(50, Bounds.Width - 230)),
            System.Math.Clamp(pos.Y, 0, System.Math.Max(0, Bounds.Height - 100)));

        TextEditorOverlay.Margin = new Thickness(pos.X, pos.Y, 0, 0);
    }

    private PageCanvas? FindCanvasForAnnotation(Annotation ann)
    {
        var items = PdfScrollViewer.Content as LayoutTransformControl;
        var itemsControl = items?.Child as ItemsControl;
        if (itemsControl == null) return null;

        foreach (var container in itemsControl.GetRealizedContainers())
        {
            var canvas = VisualTreeHelpers.FindDescendant<PageCanvas>(container);
            if (canvas?.Annotations?.Contains(ann) == true)
                return canvas;
        }
        return null;
    }
}
