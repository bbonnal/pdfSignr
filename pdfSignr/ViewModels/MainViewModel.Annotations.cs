using CommunityToolkit.Mvvm.Input;
using pdfSignr.Models;
using pdfSignr.Services;
using pdfSignr.Services.Commands;

namespace pdfSignr.ViewModels;

// Annotation creation, selection, deletion, and tool toggling
public partial class MainViewModel
{
    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Delete()
    {
        if (SelectedAnnotation == null) return;
        var ann = SelectedAnnotation;

        PageItem? ownerPage = null;
        int annIndex = -1;
        foreach (var page in Pages)
        {
            int idx = page.Annotations.IndexOf(ann);
            if (idx >= 0) { ownerPage = page; annIndex = idx; break; }
        }
        if (ownerPage == null) return;

        var cmd = new DeleteAnnotationCommand(this, ownerPage, ann, annIndex);
        UndoRedo.Execute(cmd);
    }

    private bool CanDelete => SelectedAnnotation != null;

    [RelayCommand]
    private void ToggleTextTool()
    {
        CurrentTool = CurrentTool == ToolMode.Text ? ToolMode.Select : ToolMode.Text;
    }

    [RelayCommand]
    private async Task ToggleSignTool()
    {
        if (CurrentTool == ToolMode.Signature)
        {
            CurrentTool = ToolMode.Select;
            return;
        }

        if (SignatureSvgPath == null)
        {
            var path = await _fileDialogs.PickOpenFileAsync("Select Signature", ["*.svg", "*.png", "*.jpg", "*.jpeg"]);
            if (path == null) return;
            SignatureSvgPath = path;
        }

        CurrentTool = ToolMode.Signature;
    }

    public void OnCanvasClicked(int pageIndex, double pdfX, double pdfY)
    {
        if (pageIndex < 0 || pageIndex >= Pages.Count) return;

        var page = Pages[pageIndex];

        if (CurrentTool == ToolMode.Text)
        {
            const double w = 50, h = 18;
            var annotation = new TextAnnotation
            {
                X = Math.Clamp(pdfX - w / 2, 0, Math.Max(0, page.WidthPt - w)),
                Y = Math.Clamp(pdfY - h / 2, 0, Math.Max(0, page.HeightPt - h)),
                PageIndex = pageIndex,
                Text = "Text", FontFamily = _fontCatalog.PdfFontNames[0],
                HeightPt = h, WidthPt = w
            };
            UndoRedo.Execute(new AddAnnotationCommand(this, page, annotation));
            CurrentTool = ToolMode.Select;
        }
        else if (CurrentTool == ToolMode.Signature && SignatureSvgPath != null)
        {
            bool isRaster = !SignatureSvgPath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);

            double baseW, baseH;

            if (isRaster)
            {
                (baseW, baseH) = _svgRenderer.GetImageSize(SignatureSvgPath);
            }
            else
            {
                (baseW, baseH) = _svgRenderer.GetSvgSize(SignatureSvgPath);
            }

            double fitScale = 1.0;
            if (baseW > 0 && baseH > 0)
            {
                double maxW = page.WidthPt * 0.25;
                double maxH = page.HeightPt * 0.10;
                fitScale = Math.Min(1.0, Math.Min(maxW / baseW, maxH / baseH));
            }
            double displayW = baseW * fitScale;
            double displayH = baseH * fitScale;

            Avalonia.Media.Imaging.Bitmap bitmap = isRaster
                ? _svgRenderer.ResampleForDisplay(SignatureSvgPath, displayW, displayH, PdfConstants.RenderDpi)
                : _svgRenderer.RenderForDisplay(SignatureSvgPath, fitScale, PdfConstants.RenderDpi);

            var annotation = new SvgAnnotation
            {
                X = Math.Clamp(pdfX - displayW / 2, 0, Math.Max(0, page.WidthPt - displayW)),
                Y = Math.Clamp(pdfY - displayH / 2, 0, Math.Max(0, page.HeightPt - displayH)),
                PageIndex = pageIndex,
                SvgFilePath = SignatureSvgPath,
                IsRaster = isRaster,
                Scale = fitScale,
                OriginalWidthPt = baseW, OriginalHeightPt = baseH,
                WidthPt = displayW, HeightPt = displayH,
                RenderedBitmap = bitmap,
                RenderedDpi = PdfConstants.RenderDpi
            };
            UndoRedo.Execute(new AddAnnotationCommand(this, page, annotation));
            CurrentTool = ToolMode.Select;
        }
        else
        {
            SelectAnnotation(null);
        }
    }

    public void SelectAnnotation(Annotation? annotation)
    {
        if (SelectedAnnotation != null)
            SelectedAnnotation.IsSelected = false;

        SelectedAnnotation = annotation;

        if (annotation != null)
            annotation.IsSelected = true;
    }

    /// <summary>
    /// View-layer intent: commit a pointer-driven manipulation of an annotation.
    /// The view reports old/new bounds; the VM records the reversible command.
    /// </summary>
    public void CommitAnnotationManipulation(
        Annotation ann,
        double oldX, double oldY, double oldW, double oldH, double oldRot,
        double newX, double newY, double newW, double newH, double newRot)
    {
        bool changed = newX != oldX || newY != oldY
                    || newW != oldW || newH != oldH || newRot != oldRot;
        if (!changed) return;

        UndoRedo.Push(new ManipulateAnnotationCommand(ann,
            oldX, oldY, oldW, oldH, oldRot,
            newX, newY, newW, newH, newRot));
    }

    /// <summary>
    /// View-layer intent: commit a text-annotation text/font edit from the inline editor.
    /// </summary>
    public void CommitTextEdit(TextAnnotation ann, string oldText, string oldFont, string newText, string newFont)
    {
        if (oldText == newText && oldFont == newFont) return;
        UndoRedo.Push(new EditTextAnnotationCommand(ann, oldText, oldFont, newText, newFont));
    }
}
