using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using pdfSignr.ViewModels;

namespace pdfSignr.Views;

internal static class VisualTreeHelpers
{
    /// <summary>Walks up the visual/logical tree to find a PageItem DataContext.</summary>
    public static PageItem? FindPageItemFromControl(Control control)
    {
        Control? current = control;
        while (current != null)
        {
            if (current.DataContext is PageItem page)
                return page;
            current = current.Parent as Control;
        }
        return null;
    }

    /// <summary>Depth-first search for a descendant of a specific type.</summary>
    public static T? FindDescendant<T>(Control root) where T : Control
    {
        if (root is T match) return match;
        if (root is ContentPresenter cp && cp.Child is Control cpChild)
            return FindDescendant<T>(cpChild);
        if (root is Decorator d && d.Child is Control dChild)
            return FindDescendant<T>(dChild);
        if (root is Panel p)
        {
            foreach (var child in p.Children)
            {
                if (child is Control cc)
                {
                    var found = FindDescendant<T>(cc);
                    if (found != null) return found;
                }
            }
        }
        return null;
    }
}
