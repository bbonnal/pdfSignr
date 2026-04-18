using Avalonia.Controls;
using Avalonia.Platform.Storage;
using pdfSignr.Views;

namespace pdfSignr.Services;

/// <summary>
/// Provides access to window-scoped UI services after the MainWindow is constructed.
/// Breaks the circular dependency between MainWindow, MainViewModel, and FileDialogService.
/// </summary>
public interface IWindowAccessor
{
    Window MainWindow { get; }
    IStorageProvider StorageProvider { get; }
    DialogOverlay Dialog { get; }
    void Attach(Window window, DialogOverlay dialog);
}

public class WindowAccessor : IWindowAccessor
{
    private Window? _window;
    private DialogOverlay? _dialog;

    public Window MainWindow => _window ?? throw new InvalidOperationException("MainWindow not attached yet");
    public IStorageProvider StorageProvider => MainWindow.StorageProvider;
    public DialogOverlay Dialog => _dialog ?? throw new InvalidOperationException("Dialog not attached yet");

    public void Attach(Window window, DialogOverlay dialog)
    {
        _window = window;
        _dialog = dialog;
    }
}
