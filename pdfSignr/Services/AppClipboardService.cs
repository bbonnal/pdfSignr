using pdfSignr.Models;
using pdfSignr.ViewModels;

namespace pdfSignr.Services;

/// <summary>
/// In-memory clipboard for Ctrl-C/Ctrl-V within the app. Holds either an annotation
/// (with its owning page) or a list of pages — copying one kind clears the other.
/// Nothing crosses the process boundary; this isn't a system clipboard.
/// </summary>
public class AppClipboardService
{
    public Annotation? Annotation { get; private set; }
    public PageItem? AnnotationOwner { get; private set; }
    public IReadOnlyList<PageItem> Pages { get; private set; } = Array.Empty<PageItem>();

    public bool HasAnnotation => Annotation != null;
    public bool HasPages => Pages.Count > 0;

    public void SetAnnotation(Annotation annotation, PageItem owner)
    {
        Annotation = annotation;
        AnnotationOwner = owner;
        Pages = Array.Empty<PageItem>();
    }

    public void SetPages(IReadOnlyList<PageItem> pages)
    {
        Pages = pages.ToList();
        Annotation = null;
        AnnotationOwner = null;
    }
}
