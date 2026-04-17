using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using pdfSignr.Models;
using pdfSignr.Services;

namespace pdfSignr.ViewModels;

public partial class MainViewModel : ObservableObject
{
    internal const string DefaultStatus = "Open a PDF to get started";

    private readonly IFileDialogService _fileDialogs;
    private readonly IPdfRenderService _renderService;
    private readonly IPdfSaveService _saveService;
    private readonly IPdfCompressService _compressService;

    [ObservableProperty] private string? _pdfFilePath;
    [ObservableProperty] private ObservableCollection<PageItem> _pages = new();
    [ObservableProperty] private Annotation? _selectedAnnotation;
    [ObservableProperty] private ToolMode _currentTool = ToolMode.Select;
    private string _baseStatus = DefaultStatus;
    private string BaseStatus
    {
        get => _baseStatus;
        set { _baseStatus = value; UpdateStatusText(); }
    }
    [ObservableProperty] private string _statusText = DefaultStatus;
    [ObservableProperty] private string? _signatureSvgPath;
    [ObservableProperty] private bool _isDraggingFile;
    [ObservableProperty] private double _buttonScale = 1.0;
    [ObservableProperty] private bool _isGridMode;
    [ObservableProperty] private bool _isDraggingPage;
    [ObservableProperty] private Thickness _selectionBorderThickness = new(3);
    [ObservableProperty] private bool _lockOnSave;

    public bool IsNotDraggingFile => !IsDraggingFile;
    public bool IsNotDragging => !IsDraggingFile && !IsDraggingPage;
    public bool HasNoPages => Pages.Count == 0;
    public bool IsNotGridMode => !IsGridMode;

    partial void OnIsDraggingFileChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNotDraggingFile));
        OnPropertyChanged(nameof(IsNotDragging));
    }

    partial void OnIsDraggingPageChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNotDragging));
    }

    partial void OnIsGridModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNotGridMode));
    }

    /// <summary>
    /// Prompts for an output password if LockOnSave is enabled.
    /// Returns null password if locking is off, or cancelled=true if the user dismissed the prompt.
    /// </summary>
    private async Task<(bool Cancelled, string? Password)> GetOutputPasswordAsync()
    {
        if (!LockOnSave || ShowPasswordOverlay == null) return (false, null);

        var pw = await ShowPasswordOverlay("Set Password", "Enter a password to lock the saved PDF.", false);
        if (pw == null) return (true, null);
        return (false, pw);
    }

    public void SetDropTarget(int insertIndex)
    {
        ClearDropTargets();
        if (insertIndex == 0 && Pages.Count > 0)
            Pages[0].IsDropTargetBefore = true;
        else if (insertIndex > 0 && insertIndex <= Pages.Count)
            Pages[insertIndex - 1].IsDropTargetAfter = true;
    }

    public void ClearDropTargets()
    {
        foreach (var page in Pages)
        {
            page.IsDropTargetAfter = false;
            page.IsDropTargetBefore = false;
        }
    }

    public static string[] AvailableFonts => FontResolver.PdfFontNames;

    // Set by MainWindow when zoom changes
    public int ZoomPercent { get; set; } = 100;

    // Computed tool-active properties for Ribbon ToggleButton binding
    public bool IsTextToolActive => CurrentTool == ToolMode.Text;
    public bool IsSignToolActive => CurrentTool == ToolMode.Signature;
    public string TextToolTip => IsTextToolActive ? "Click on page to place text" : "Add text";
    public string SignToolTip => IsSignToolActive ? "Click on page to place signature" : "Add signature";
    private static readonly Cursor DragMoveCursor = new(StandardCursorType.DragMove);
    public Cursor? PlacementCursor => CurrentTool is ToolMode.Text or ToolMode.Signature
        ? DragMoveCursor : null;

    public event Action? PdfLoaded;
    public event Action<PageItem>? PageRotated;

    /// <summary>Set by the View to show a password overlay. Args: title, message, showError. Returns password or null.</summary>
    public Func<string, string, bool, Task<string?>>? ShowPasswordOverlay { get; set; }

    public UndoRedoService UndoRedo { get; } = new();

    public IPdfRenderService RenderService => _renderService;

    public MainViewModel(IFileDialogService fileDialogs, IPdfRenderService renderService,
        IPdfSaveService saveService, IPdfCompressService compressService)
    {
        _fileDialogs = fileDialogs;
        _renderService = renderService;
        _saveService = saveService;
        _compressService = compressService;
        Pages.CollectionChanged += OnPagesCollectionChanged;
        UndoRedo.PropertyChanged += (_, _) =>
        {
            UndoCommand.NotifyCanExecuteChanged();
            RedoCommand.NotifyCanExecuteChanged();
        };
    }

    partial void OnPagesChanged(ObservableCollection<PageItem>? oldValue, ObservableCollection<PageItem> newValue)
    {
        if (oldValue != null) oldValue.CollectionChanged -= OnPagesCollectionChanged;
        newValue.CollectionChanged += OnPagesCollectionChanged;
        NotifyCollectionDependents();
    }

    private void OnPagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => NotifyCollectionDependents();

    private void NotifyCollectionDependents()
    {
        SaveCommand.NotifyCanExecuteChanged();
        CompressResampleCommand.NotifyCanExecuteChanged();
        CompressRasterizeCommand.NotifyCanExecuteChanged();
        SavePageRangeCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasNoPages));
    }

    partial void OnSelectedAnnotationChanged(Annotation? oldValue, Annotation? newValue)
    {
        if (oldValue != null) oldValue.PropertyChanged -= OnAnnotationPropChanged;
        if (newValue != null) newValue.PropertyChanged += OnAnnotationPropChanged;
        DeleteCommand.NotifyCanExecuteChanged();
        UpdateStatusText();
    }

    private void OnAnnotationPropChanged(object? sender, PropertyChangedEventArgs e) => UpdateStatusText();

    public void UpdateStatusText()
    {
        var info = SelectedAnnotation switch
        {
            TextAnnotation t => $"  \u2502  {t.FontFamily}  {t.FontSize:F1} pt" +
                                (t.Rotation != 0 ? $"  {t.Rotation:F0}\u00b0" : ""),
            SvgAnnotation s => $"  \u2502  {s.WidthPt:F0} \u00d7 {s.HeightPt:F0} pt" +
                               (s.Rotation != 0 ? $"  {s.Rotation:F0}\u00b0" : ""),
            _ => ""
        };
        var selInfo = SelectedPageCount > 1 ? $"  \u2502  {SelectedPageCount} pages selected" : "";
        StatusText = $"{_baseStatus}{info}{selInfo}  \u2502  {ZoomPercent}%";
    }

    // CanSave depends on Pages.Count, not PdfFilePath — notifications are in OnPagesCollectionChanged

    partial void OnCurrentToolChanged(ToolMode value)
    {
        OnPropertyChanged(nameof(IsTextToolActive));
        OnPropertyChanged(nameof(IsSignToolActive));
        OnPropertyChanged(nameof(TextToolTip));
        OnPropertyChanged(nameof(SignToolTip));
        OnPropertyChanged(nameof(PlacementCursor));
    }

    // --- Commands ---

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => UndoRedo.Undo();
    private bool CanUndo => UndoRedo.CanUndo;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => UndoRedo.Redo();
    private bool CanRedo => UndoRedo.CanRedo;
}
