using CommunityToolkit.Mvvm.Input;
using pdfSignr.Models;
using pdfSignr.Services;

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

        ownerPage.Annotations.RemoveAt(annIndex);
        SelectAnnotation(null);

        UndoRedo.Push(new UndoEntry(
            "Delete annotation",
            Undo: () => { ownerPage.Annotations.Insert(annIndex, ann); SelectAnnotation(ann); },
            Redo: () => { ownerPage.Annotations.Remove(ann); SelectAnnotation(null); }
        ));
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
                Text = "Text", FontFamily = FontResolver.PdfFontNames[0],
                HeightPt = h, WidthPt = w
            };
            page.Annotations.Add(annotation);
            SelectAnnotation(annotation);
            CurrentTool = ToolMode.Select;

            UndoRedo.Push(new UndoEntry(
                "Add text",
                Undo: () => { page.Annotations.Remove(annotation); SelectAnnotation(null); },
                Redo: () => { page.Annotations.Add(annotation); SelectAnnotation(annotation); }
            ));
        }
        else if (CurrentTool == ToolMode.Signature && SignatureSvgPath != null)
        {
            bool isRaster = !SignatureSvgPath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);

            double baseW, baseH;
            Avalonia.Media.Imaging.Bitmap bitmap;

            if (isRaster)
            {
                (baseW, baseH) = SvgRenderService.GetImageSize(SignatureSvgPath);
                bitmap = new Avalonia.Media.Imaging.Bitmap(SignatureSvgPath);
            }
            else
            {
                (baseW, baseH, bitmap) = SvgRenderService.GetSizeAndRenderForDisplay(SignatureSvgPath, 1.0, PdfConstants.RenderDpi);
            }

            var annotation = new SvgAnnotation
            {
                X = Math.Clamp(pdfX - baseW / 2, 0, Math.Max(0, page.WidthPt - baseW)),
                Y = Math.Clamp(pdfY - baseH / 2, 0, Math.Max(0, page.HeightPt - baseH)),
                PageIndex = pageIndex,
                SvgFilePath = SignatureSvgPath,
                IsRaster = isRaster,
                Scale = 1.0,
                OriginalWidthPt = baseW, OriginalHeightPt = baseH,
                WidthPt = baseW, HeightPt = baseH,
                RenderedBitmap = bitmap,
                RenderedDpi = PdfConstants.RenderDpi
            };
            page.Annotations.Add(annotation);
            SelectAnnotation(annotation);
            CurrentTool = ToolMode.Select;

            UndoRedo.Push(new UndoEntry(
                "Add signature",
                Undo: () => { page.Annotations.Remove(annotation); SelectAnnotation(null); },
                Redo: () => { page.Annotations.Add(annotation); SelectAnnotation(annotation); }
            ));
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
}
