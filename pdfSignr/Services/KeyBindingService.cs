namespace pdfSignr.Services;

public record KeyBinding(string Keys, string Description, string Category);

public static class KeyBindingService
{
    public static IReadOnlyList<KeyBinding> Bindings { get; } =
    [
        // Navigation
        new("↑",        "Scroll up one page",              "Navigation"),
        new("↓",        "Scroll down one page",            "Navigation"),
        new("Enter",    "Select center page",              "Navigation"),

        // Selection
        new("Ctrl+A",   "Select all pages",                "Selection"),
        new("Ctrl+↑",   "Add page above to selection",     "Selection"),
        new("Ctrl+↓",   "Add page below to selection",     "Selection"),
        new("Esc",      "Deselect all / cancel tool",      "Selection"),
        new("Click",    "Select page",                     "Selection"),
        new("Ctrl+Click", "Toggle page in selection",      "Selection"),
        new("Shift+Click","Range select pages",            "Selection"),

        // Page operations
        new("R",        "Rotate selected pages clockwise", "Pages"),
        new("Shift+R",  "Rotate selected pages counter-clockwise", "Pages"),
        new("Del",      "Delete selected pages",           "Pages"),
        new("M",        "Move selected pages up/left",     "Pages"),
        new("Shift+M",  "Move selected pages down/right",  "Pages"),
        new("Drag border", "Reorder by dragging selection border", "Pages"),

        // Tools
        new("A",        "Add text annotation",             "Tools"),
        new("S",        "Add signature",                   "Tools"),

        // File
        new("Ctrl+O",   "Open PDF",                        "File"),
        new("Ctrl+S",   "Save (selected or all)",          "File"),
        new("Ctrl+Z",   "Undo",                            "File"),
        new("Ctrl+Y",   "Redo",                            "File"),

        // View
        new("0",        "Fit to width",                    "View"),
        new("+",        "Zoom in",                         "View"),
        new("−",        "Zoom out",                        "View"),
        new("G",        "Toggle grid/list mode",           "View"),
    ];
}
