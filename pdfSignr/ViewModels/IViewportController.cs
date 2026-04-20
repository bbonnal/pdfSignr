namespace pdfSignr.ViewModels;

/// <summary>
/// View-layer operations invoked by services (key bindings, menus) that need to
/// drive zoom/scroll/selection without referencing the concrete View class.
/// The MainWindow implements this; services depend on the interface only.
/// </summary>
public interface IViewportController
{
    void FitToWidth();
    void FitToHeight();
    void ZoomIn();
    void ZoomOut();
    void ScrollByPages(int delta);
    void SelectCenterPage();
    void SelectAdjacentPage(int delta, bool addToSelection);
    void HideTextEditor();
    void ApplyGridMode(bool gridMode);
    PageItem? GetSelectedOrVisiblePage();
}
