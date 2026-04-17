using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PdfSharp.Fonts;
using pdfSignr.Services;
using pdfSignr.ViewModels;
using pdfSignr.Views;

namespace pdfSignr;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        if (GlobalFontSettings.FontResolver == null)
            GlobalFontSettings.FontResolver = new FontResolver();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            var dialogOverlay = window.FindControl<DialogOverlay>("Dialog")!;
            var fileDialogs = new FileDialogService(window.StorageProvider, dialogOverlay);
            var renderService = new PdfRenderService();
            var saveService = new PdfSaveService();
            var compressService = new PdfCompressService(renderService);
            var vm = new MainViewModel(fileDialogs, renderService, saveService, compressService);
            window.DataContext = vm;
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
