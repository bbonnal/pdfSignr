using Avalonia;
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
            var fileDialogs = new FileDialogService(window.StorageProvider);
            var vm = new MainViewModel(fileDialogs);
            window.DataContext = vm;
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
