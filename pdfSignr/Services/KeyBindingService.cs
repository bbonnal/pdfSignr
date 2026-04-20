using Avalonia.Input;
using pdfSignr.ViewModels;

namespace pdfSignr.Services;

public record KeyChord(Key Key, KeyModifiers Modifiers);

public record KeyBinding(
    string Id,
    KeyChord Chord,
    string DisplayText,
    string Description,
    string Category,
    Action<MainViewModel, IViewportController> Handler);

public interface IKeyBindingService
{
    IReadOnlyList<KeyBinding> Bindings { get; }

    /// <summary>Rows to display in the help flyout, grouped by category (hotkey-only; no mouse entries).</summary>
    IReadOnlyList<KeyBindingDisplay> DisplayRows { get; }

    /// <summary>Attempts to dispatch the chord. Returns true if a handler was invoked.</summary>
    bool TryDispatch(KeyChord chord, MainViewModel vm, IViewportController viewport);
}

public record KeyBindingDisplay(string DisplayText, string Description, string Category);

public class KeyBindingService : IKeyBindingService
{
    private readonly Dictionary<KeyChord, KeyBinding> _byChord;
    public IReadOnlyList<KeyBinding> Bindings { get; }
    public IReadOnlyList<KeyBindingDisplay> DisplayRows { get; }

    public KeyBindingService(ISettingsService settings)
    {
        Bindings = BuildDefaults();
        _byChord = Bindings.ToDictionary(b => b.Chord, b => b);
        DisplayRows = BuildDisplayRows(Bindings);
        // Setting-based overrides are an explicit feature handle; not loaded here to keep
        // the default-behavior surface small until a real override UI exists.
        _ = settings;
    }

    public bool TryDispatch(KeyChord chord, MainViewModel vm, IViewportController viewport)
    {
        if (!_byChord.TryGetValue(chord, out var binding)) return false;
        binding.Handler(vm, viewport);
        return true;
    }

    private static IReadOnlyList<KeyBinding> BuildDefaults()
    {
        var list = new List<KeyBinding>();

        void Add(string id, KeyChord chord, string display, string desc, string cat,
            Action<MainViewModel, IViewportController> handler)
            => list.Add(new KeyBinding(id, chord, display, desc, cat, handler));

        // ═══ File ═══
        Add("open", new(Key.O, KeyModifiers.Control), "Ctrl+O", "Open PDF", "File",
            (vm, _) => vm.OpenCommand.Execute(null));
        Add("save", new(Key.S, KeyModifiers.Control), "Ctrl+S", "Save (selected or all)", "File",
            (vm, _) =>
            {
                if (vm.HasSelectedPages && vm.SaveSelectedPagesCommand.CanExecute(null))
                    vm.SaveSelectedPagesCommand.Execute(null);
                else if (vm.SaveCommand.CanExecute(null))
                    vm.SaveCommand.Execute(null);
            });
        Add("undo", new(Key.Z, KeyModifiers.Control), "Ctrl+Z", "Undo", "File",
            (vm, _) => { if (vm.UndoCommand.CanExecute(null)) vm.UndoCommand.Execute(null); });
        Add("redo", new(Key.Y, KeyModifiers.Control), "Ctrl+Y", "Redo", "File",
            (vm, _) => { if (vm.RedoCommand.CanExecute(null)) vm.RedoCommand.Execute(null); });
        Add("redo-alt", new(Key.Z, KeyModifiers.Control | KeyModifiers.Shift), "Ctrl+Shift+Z", "Redo", "File",
            (vm, _) => { if (vm.RedoCommand.CanExecute(null)) vm.RedoCommand.Execute(null); });

        // ═══ Selection ═══
        Add("select-all", new(Key.A, KeyModifiers.Control), "Ctrl+A", "Select all pages", "Selection",
            (vm, _) => vm.SelectAllPages());
        Add("select-up", new(Key.Up, KeyModifiers.Control), "Ctrl+↑", "Add page above to selection", "Selection",
            (_, vp) => vp.SelectAdjacentPage(-1, addToSelection: true));
        Add("select-down", new(Key.Down, KeyModifiers.Control), "Ctrl+↓", "Add page below to selection", "Selection",
            (_, vp) => vp.SelectAdjacentPage(1, addToSelection: true));
        Add("deselect", new(Key.Escape, KeyModifiers.None), "Esc", "Deselect all / cancel tool", "Selection",
            (vm, vp) =>
            {
                vp.HideTextEditor();
                vm.CurrentTool = ToolMode.Select;
                vm.SelectAnnotation(null);
                if (vm.HasSelectedPages) vm.ClearPageSelection();
            });

        // ═══ Pages ═══
        Add("rotate-cw", new(Key.R, KeyModifiers.None), "R", "Rotate selected pages clockwise", "Pages",
            (vm, vp) =>
            {
                var t = vp.GetSelectedOrVisiblePage();
                if (t != null) vm.RotatePageCwCommand.Execute(t);
            });
        Add("rotate-ccw", new(Key.R, KeyModifiers.Shift), "Shift+R", "Rotate selected pages counter-clockwise", "Pages",
            (vm, vp) =>
            {
                var t = vp.GetSelectedOrVisiblePage();
                if (t != null) vm.RotatePageCcwCommand.Execute(t);
            });
        Add("move-up", new(Key.M, KeyModifiers.None), "M", "Move selected pages up/left", "Pages",
            (vm, vp) =>
            {
                var t = vp.GetSelectedOrVisiblePage();
                if (t != null) vm.MovePageUpCommand.Execute(t);
            });
        Add("move-down", new(Key.M, KeyModifiers.Shift), "Shift+M", "Move selected pages down/right", "Pages",
            (vm, vp) =>
            {
                var t = vp.GetSelectedOrVisiblePage();
                if (t != null) vm.MovePageDownCommand.Execute(t);
            });
        Add("delete", new(Key.Delete, KeyModifiers.None), "Del", "Delete annotation / selected pages", "Pages",
            (vm, vp) => DeleteSelected(vm, vp));
        Add("delete-back", new(Key.Back, KeyModifiers.None), "Backspace", "Delete annotation / selected pages", "Pages",
            (vm, vp) => DeleteSelected(vm, vp));

        // ═══ Tools ═══
        Add("tool-text", new(Key.A, KeyModifiers.None), "A", "Add text annotation", "Tools",
            (vm, _) => vm.ToggleTextToolCommand.Execute(null));
        Add("tool-sign", new(Key.S, KeyModifiers.None), "S", "Add signature", "Tools",
            (vm, _) => vm.ToggleSignToolCommand.Execute(null));

        // ═══ Navigation ═══
        Add("scroll-up", new(Key.Up, KeyModifiers.None), "↑", "Scroll up one page", "Navigation",
            (_, vp) => vp.ScrollByPages(-1));
        Add("scroll-down", new(Key.Down, KeyModifiers.None), "↓", "Scroll down one page", "Navigation",
            (_, vp) => vp.ScrollByPages(1));
        Add("select-center", new(Key.Enter, KeyModifiers.None), "Enter", "Select center page", "Navigation",
            (_, vp) => vp.SelectCenterPage());

        // ═══ View ═══
        Add("fit-width-ctrl", new(Key.D0, KeyModifiers.Control), "Ctrl+0", "Fit to width", "View",
            (_, vp) => vp.FitToWidth());
        Add("fit-width", new(Key.D0, KeyModifiers.None), "0", "Fit to width", "View",
            (_, vp) => vp.FitToWidth());
        Add("fit-height-ctrl", new(Key.D1, KeyModifiers.Control), "Ctrl+1", "Fit to height", "View",
            (_, vp) => vp.FitToHeight());
        Add("fit-height", new(Key.D1, KeyModifiers.None), "1", "Fit to height", "View",
            (_, vp) => vp.FitToHeight());
        Add("zoom-in-ctrl-plus", new(Key.OemPlus, KeyModifiers.Control), "Ctrl++", "Zoom in", "View",
            (_, vp) => vp.ZoomIn());
        Add("zoom-in-ctrl-add", new(Key.Add, KeyModifiers.Control), "Ctrl++", "Zoom in", "View",
            (_, vp) => vp.ZoomIn());
        Add("zoom-in-plus", new(Key.OemPlus, KeyModifiers.None), "+", "Zoom in", "View",
            (_, vp) => vp.ZoomIn());
        Add("zoom-in-add", new(Key.Add, KeyModifiers.None), "+", "Zoom in", "View",
            (_, vp) => vp.ZoomIn());
        Add("zoom-out-ctrl-minus", new(Key.OemMinus, KeyModifiers.Control), "Ctrl+-", "Zoom out", "View",
            (_, vp) => vp.ZoomOut());
        Add("zoom-out-ctrl-subtract", new(Key.Subtract, KeyModifiers.Control), "Ctrl+-", "Zoom out", "View",
            (_, vp) => vp.ZoomOut());
        Add("zoom-out-minus", new(Key.OemMinus, KeyModifiers.None), "−", "Zoom out", "View",
            (_, vp) => vp.ZoomOut());
        Add("zoom-out-subtract", new(Key.Subtract, KeyModifiers.None), "−", "Zoom out", "View",
            (_, vp) => vp.ZoomOut());
        Add("toggle-grid", new(Key.G, KeyModifiers.None), "G", "Toggle grid/list mode", "View",
            (vm, vp) => { vm.Viewport.IsGridMode = !vm.Viewport.IsGridMode; vp.ApplyGridMode(vm.Viewport.IsGridMode); });

        return list;
    }

    private static void DeleteSelected(MainViewModel vm, IViewportController vp)
    {
        if (vm.DeleteCommand.CanExecute(null))
        {
            vp.HideTextEditor();
            vm.DeleteCommand.Execute(null);
        }
        else if (vm.HasSelectedPages)
        {
            vm.DeleteSelectedPagesCommand.Execute(null);
        }
    }

    /// <summary>
    /// Groups bindings into unique display rows for the help flyout. Rows with identical
    /// DisplayText+Description are collapsed (e.g. the several flavors of Zoom-In become one row).
    /// Mouse-only actions are provided alongside for completeness.
    /// </summary>
    private static IReadOnlyList<KeyBindingDisplay> BuildDisplayRows(IReadOnlyList<KeyBinding> bindings)
    {
        var seen = new HashSet<(string, string)>();
        var rows = new List<KeyBindingDisplay>();
        foreach (var b in bindings)
        {
            var key = (b.DisplayText, b.Description);
            if (!seen.Add(key)) continue;
            rows.Add(new KeyBindingDisplay(b.DisplayText, b.Description, b.Category));
        }

        // Mouse-only rows (no key chord, so not part of Bindings)
        rows.Add(new("Click", "Select page", "Selection"));
        rows.Add(new("Ctrl+Click", "Toggle page in selection", "Selection"));
        rows.Add(new("Shift+Click", "Range select pages", "Selection"));
        rows.Add(new("Drag border", "Reorder by dragging selection border", "Pages"));

        // Sort by category in canonical order, stable within category
        var order = new Dictionary<string, int>
        {
            ["Navigation"] = 0, ["Selection"] = 1, ["Pages"] = 2,
            ["Tools"] = 3, ["File"] = 4, ["View"] = 5
        };
        return rows
            .OrderBy(r => order.GetValueOrDefault(r.Category, 99))
            .ToList();
    }
}
