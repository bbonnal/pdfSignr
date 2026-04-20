using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Iconr;
using Microsoft.Extensions.DependencyInjection;
using pdfSignr.Models;
using pdfSignr.Services;
using pdfSignr.ViewModels;

namespace pdfSignr.Views;

public partial class MainWindow : Window, IViewportController
{
    private const double FitToWidthPadding = 40;
    private const double ScrollIntoViewMargin = 20;
    private const double DragThumbnailWidth = 80;
    // 28px invisible outer grab zone + 12px inward from the page edge = 40px total
    private const double BorderHitZone = 40;

    private readonly IKeyBindingService _keyBindingService;
    private readonly ISettingsService _settingsService;
    private readonly AppSettings _settings;

    private double ZoomFactor => _settings.ZoomFactor;
    private double MinZoom => _settings.MinZoom;
    private double MaxZoom => _settings.MaxZoom;

    private MainViewModel ViewModel => (MainViewModel)DataContext!;

    // Parameterless constructor used by the Avalonia XAML designer only.
    // At runtime the DI-registered constructor below is resolved.
    public MainWindow()
        : this(App.Services.GetRequiredService<IKeyBindingService>(),
               App.Services.GetRequiredService<ISettingsService>())
    {
    }

    public MainWindow(IKeyBindingService keyBindingService, ISettingsService settingsService)
    {
        _keyBindingService = keyBindingService;
        _settingsService = settingsService;
        _settings = settingsService.Current;

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

        // Flyout population is deferred to OnLoaded; it requires the theme resources
        // to be resolvable via the visual tree, which isn't true at ctor time.
    }

    private void PopulateKeyBindingsFlyout()
    {
        string? lastCategory = null;
        var rows = _keyBindingService.DisplayRows;
        for (int i = 0; i < rows.Count; i++)
        {
            var kb = rows[i];
            if (kb.Category != lastCategory)
            {
                lastCategory = kb.Category;
                KeyBindingsFlyout.Children.Add(new TextBlock
                {
                    Text = kb.Category,
                    FontSize = 11,
                    FontWeight = FontWeight.SemiBold,
                    Opacity = 0.6,
                    Margin = new Thickness(0, i == 0 ? 0 : 8, 0, 2)
                });
            }

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            IBrush keyBadgeBrush = Brushes.Transparent;
            if (TryGetResource("KeyBadgeBrush", Application.Current?.ActualThemeVariant, out var res) && res is IBrush b)
                keyBadgeBrush = b;
            var keyBorder = new Border
            {
                Background = keyBadgeBrush,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 1),
                Margin = new Thickness(0, 0, 10, 0),
                Child = new TextBlock
                {
                    Text = kb.DisplayText,
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

        // Apply persisted theme variant, then populate the flyout (needs themed resources).
        ApplyThemeFromSettings();
        PopulateKeyBindingsFlyout();
        Application.Current!.ActualThemeVariantChanged += OnThemeChanged;

        // Warm up the StorageProvider so the first file-open dialog is fast.
        // On Linux/WSL this forces the D-Bus portal connection to initialize in the background.
        _ = StorageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Documents);
    }

    private void ApplyThemeFromSettings()
    {
        var variant = _settings.ThemeVariant switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
        Application.Current!.RequestedThemeVariant = variant;
        UpdateThemeIcon();
    }

    private void UpdateThemeIcon()
    {
        var isDark = Application.Current!.ActualThemeVariant == ThemeVariant.Dark;
        ThemeIcon.Data = IconService.CreateGeometry(isDark ? Iconr.Icon.sun : Iconr.Icon.moon);
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        UpdateThemeIcon();
        KeyBindingsFlyout.Children.Clear();
        PopulateKeyBindingsFlyout();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (Application.Current != null)
            Application.Current.ActualThemeVariantChanged -= OnThemeChanged;
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
        _renderGate.Dispose();
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
        ViewModel.CommitAnnotationManipulation(e.Annotation,
            e.OldX, e.OldY, e.OldW, e.OldH, e.OldRot,
            e.NewX, e.NewY, e.NewW, e.NewH, e.NewRot);
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

    private void OnThemeToggle(object? sender, RoutedEventArgs e)
    {
        var current = Application.Current!.ActualThemeVariant;
        var next = current == ThemeVariant.Dark ? ThemeVariant.Light : ThemeVariant.Dark;
        Application.Current.RequestedThemeVariant = next;
        _settingsService.Update(s => s with { ThemeVariant = next == ThemeVariant.Dark ? "Dark" : "Light" });
    }
}
