using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Debug;
using PdfSharp.Fonts;
using pdfSignr.Services;
using pdfSignr.ViewModels;
using pdfSignr.Views;

namespace pdfSignr;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    public override void Initialize()
    {
        // Build services before XAML is loaded so styles/templates that depend on
        // DI resolve cleanly.
        Services = BuildServices();

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

            var windows = Services.GetRequiredService<IWindowAccessor>();
            windows.Attach(window, dialogOverlay);

            var vm = Services.GetRequiredService<MainViewModel>();
            window.DataContext = vm;
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static IServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        // Logging
        services.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Information);
            b.AddDebug();
        });

        // App services
        services.AddSingleton<IWindowAccessor, WindowAccessor>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IFileDialogService, FileDialogService>();
        services.AddSingleton<IPdfRenderService, PdfRenderService>();
        services.AddSingleton<IPdfSaveService, PdfSaveService>();
        services.AddSingleton<IPdfCompressService, PdfCompressService>();
        services.AddSingleton<IKeyBindingService, KeyBindingService>();

        // ViewModels
        services.AddSingleton<MainViewModel>();

        return services.BuildServiceProvider();
    }
}
