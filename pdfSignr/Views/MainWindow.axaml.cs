using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Iconr;
using pdfSignr.Models;
using pdfSignr.Services;
using pdfSignr.ViewModels;

namespace pdfSignr.Views;

public partial class MainWindow : Window
{
    private const double ZoomFactor = 1.1; // 10% per step, multiplicative
    private const double MinZoom = 0.1;
    private const double MaxZoom = 5.0;
    private const double FitToWidthPadding = 40;
    private const double ScrollIntoViewMargin = 20;
    private const double DragThumbnailWidth = 80;
    // 14px invisible outer grab zone + 8px inward from the page edge = 22px total
    private const double BorderHitZone = 22;

    private MainViewModel ViewModel => (MainViewModel)DataContext!;

    public MainWindow()
    {
        InitializeComponent();

        AddHandler(PageCanvas.CanvasClickedEvent, OnCanvasClicked);
        AddHandler(PageCanvas.AnnotationSelectedEvent, OnAnnotationSelected);
        AddHandler(PageCanvas.DeleteRequestedEvent, OnDeleteRequested);
        AddHandler(PageCanvas.AnnotationManipulatedEvent, OnAnnotationManipulated);
        // Tunnel handler for page selection — fires before PageCanvas consumes the event
        AddHandler(PointerPressedEvent, OnPageAreaPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPageAreaPointerMoved, RoutingStrategies.Tunnel);
        // Tunnel so we get the event before ScrollViewer consumes it
        PdfScrollViewer.AddHandler(PointerWheelChangedEvent, OnScrollWheel, RoutingStrategies.Tunnel);

        // Re-render newly visible pages after scrolling stops
        PdfScrollViewer.ScrollChanged += OnScrollChanged;

        // Drag-and-drop for PDF files — accept drops anywhere in the window
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);

        PopulateKeyBindingsFlyout();
    }

    private void PopulateKeyBindingsFlyout()
    {
        string? lastCategory = null;
        foreach (var kb in KeyBindingService.Bindings)
        {
            if (kb.Category != lastCategory)
            {
                lastCategory = kb.Category;
                KeyBindingsFlyout.Children.Add(new TextBlock
                {
                    Text = kb.Category,
                    FontSize = 11,
                    FontWeight = FontWeight.SemiBold,
                    Opacity = 0.6,
                    Margin = new Thickness(0, lastCategory == KeyBindingService.Bindings[0].Category ? 0 : 8, 0, 2)
                });
            }

            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            };
            var keyBorder = new Border
            {
                Background = Brushes.Gray,
                Opacity = 0.15,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 1),
                Margin = new Thickness(0, 0, 10, 0),
                Child = new TextBlock
                {
                    Text = kb.Keys,
                    FontSize = 11,
                    FontWeight = FontWeight.SemiBold,
                }
            };
            var desc = new TextBlock
            {
                Text = kb.Description,
                FontSize = 11,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            Grid.SetColumn(keyBorder, 0);
            Grid.SetColumn(desc, 1);
            row.Children.Add(keyBorder);
            row.Children.Add(desc);
            KeyBindingsFlyout.Children.Add(row);
        }
    }

    // ═══ Lifecycle ═══

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // Size window to full vertical extent of the current screen
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen != null)
        {
            var scaling = screen.Scaling;
            _screenScaling = scaling;
            var workArea = screen.WorkingArea;

            // Account for window frame decorations (title bar + borders)
            double frameOverhead = FrameSize is { } frame
                ? frame.Height - ClientSize.Height
                : 32; // safe fallback for typical title bar

            double dipHeight = workArea.Height / scaling - frameOverhead;
            double dipWidth = Math.Max(600, workArea.Width / scaling * 0.6);
            Height = dipHeight;
            Width = dipWidth;
            Position = new PixelPoint(
                workArea.X + (int)((workArea.Width - dipWidth * scaling) / 2),
                workArea.Y);
        }
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.PdfLoaded += OnPdfLoaded;
        ViewModel.PageStructureChanged += OnPageStructureChanged;
        ViewModel.PageRotated += OnPageRotated;
        ViewModel.ShowPasswordOverlay = (title, msg, err) =>
            Dialog.ShowPasswordAsync(title, msg, err ? "Incorrect password. Please try again." : null)
                  .ContinueWith(t => t.Result.Confirmed ? t.Result.Text : null,
                      TaskScheduler.FromCurrentSynchronizationContext());

        // Warm up the StorageProvider so the first file-open dialog is fast.
        // On Linux/WSL this forces the D-Bus portal connection to initialize in the background.
        _ = StorageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Documents);
    }

    protected override void OnClosed(EventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.PdfLoaded -= OnPdfLoaded;
        ViewModel.PageStructureChanged -= OnPageStructureChanged;
        ViewModel.PageRotated -= OnPageRotated;
        _rerenderTimer?.Stop();
        _rerenderTimer = null;
        _scrollTimer?.Stop();
        _scrollTimer = null;
        _rerenderCts?.Cancel();
        _rerenderCts?.Dispose();
        _rerenderCts = null;
        _backgroundLoadCts?.Cancel();
        _backgroundLoadCts?.Dispose();
        _backgroundLoadCts = null;
        HideTextEditor();
        base.OnClosed(e);
    }

    // ═══ ViewModel tracking ═══

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedAnnotation))
        {
            UpdateTextEditor();
        }
    }

    private void OnAnnotationManipulated(object? sender, AnnotationManipulatedEventArgs e)
    {
        var ann = e.Annotation;
        double oldX = e.OldX, oldY = e.OldY, oldW = e.OldW, oldH = e.OldH, oldRot = e.OldRot;
        double newX = e.NewX, newY = e.NewY, newW = e.NewW, newH = e.NewH, newRot = e.NewRot;

        ViewModel.UndoRedo.Push(new UndoEntry(
            "Move/resize annotation",
            Undo: () =>
            {
                ann.X = oldX; ann.Y = oldY; ann.WidthPt = oldW; ann.HeightPt = oldH; ann.Rotation = oldRot;
                if (ann is SvgAnnotation svg)
                {
                    svg.Scale = svg.OriginalWidthPt > 0 ? oldW / svg.OriginalWidthPt : 1;
                    svg.ReRender(PdfConstants.RenderDpi);
                }
            },
            Redo: () =>
            {
                ann.X = newX; ann.Y = newY; ann.WidthPt = newW; ann.HeightPt = newH; ann.Rotation = newRot;
                if (ann is SvgAnnotation svg)
                {
                    svg.Scale = svg.OriginalWidthPt > 0 ? newW / svg.OriginalWidthPt : 1;
                    svg.ReRender(PdfConstants.RenderDpi);
                }
            }
        ));
    }

    // ═══ Page selection ═══

    private void OnPageAreaPressed(object? sender, PointerPressedEventArgs e)
    {
        // Walk up from the source to find a PageItem DataContext
        var source = e.Source as Control;
        var page = source != null ? VisualTreeHelpers.FindPageItemFromControl(source) : null;
        if (page == null)
        {
            if (ViewModel.HasSelectedPages)
                ViewModel.ClearPageSelection();
            return;
        }

        // Right-click: auto-select for context menu, then let the event bubble
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            if (!page.IsSelected)
                ViewModel.SelectPage(page, ctrl: false, shift: false);
            return;
        }

        // Don't change selection when clicking per-page action buttons or drag handle
        if (IsPageActionControl(source))
            return;

        // Clicking on the selection border zone of a selected page → initiate drag
        if (page.IsSelected)
        {
            var selBorder = FindSelectionBorder(source);
            if (selBorder != null && IsInBorderZone(e.GetPosition(selBorder), selBorder.Bounds))
            {
                BeginPageDragFromBorder(page, selBorder, e);
                return;
            }
        }

        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        // Only update selection on Ctrl/Shift clicks, or plain clicks (don't set Handled — let annotation interaction proceed)
        if (ctrl || shift)
            ViewModel.SelectPage(page, ctrl, shift);
        else if (!page.IsSelected || ViewModel.SelectedPageCount > 1)
        {
            // Plain click on unselected page or multi-selected: exclusive select
            ViewModel.SelectPage(page, false, false);
        }
    }

    private static bool IsPageActionControl(Control? source)
    {
        Control? current = source;
        while (current != null)
        {
            if (current is Button btn && btn.Classes.Contains("insert-btn"))
                return true;
            current = current.Parent as Control;
        }
        return false;
    }

    private static Border? FindSelectionBorder(Control? source)
    {
        Control? current = source;
        while (current != null)
        {
            if (current is Border b && (b.Classes.Contains("page-select-hit") || b.Classes.Contains("page-select")))
                return b;
            current = current.Parent as Control;
        }
        return null;
    }

    private bool IsInBorderZone(Point pos, Rect bounds)
    {
        double hitZone = BorderHitZone / _zoom;
        return pos.X < hitZone || pos.X > bounds.Width - hitZone
            || pos.Y < hitZone || pos.Y > bounds.Height - hitZone;
    }

    // ═══ Canvas events ═══

    private void OnCanvasClicked(object? sender, CanvasClickedEventArgs e)
    {
        HideTextEditor();
        ViewModel.OnCanvasClicked(e.PageIndex, e.PdfX, e.PdfY);
    }

    private void OnAnnotationSelected(object? sender, AnnotationSelectedEventArgs e)
    {
        ViewModel.SelectAnnotation(e.Annotation);
    }

    private void OnDeleteRequested(object? sender, RoutedEventArgs e)
    {
        HideTextEditor();
        if (ViewModel.DeleteCommand.CanExecute(null))
            ViewModel.DeleteCommand.Execute(null);
    }

    // ═══ Save flyout ═══

    private void OnSaveRangeClick(object? sender, RoutedEventArgs e)
    {
        var text = SaveRangeBox.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        if (ViewModel.SavePageRangeCommand.CanExecute(text))
            ViewModel.SavePageRangeCommand.Execute(text);
    }

    // ═══ Theme toggle ═══

    private bool _isDark = true; // dark by default

    private void OnThemeToggle(object? sender, RoutedEventArgs e)
    {
        _isDark = !_isDark;
        Application.Current!.RequestedThemeVariant = _isDark ? ThemeVariant.Dark : ThemeVariant.Light;

        ThemeIcon.Data = IconService.CreateGeometry(_isDark ? Iconr.Icon.sun : Iconr.Icon.moon);
    }
}
