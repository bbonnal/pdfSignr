using Avalonia;
using Avalonia.Dialogs;
using pdfSignr.Views;

namespace pdfSignr;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseManagedSystemDialogs<FileDialogWindow>();
#if DEBUG
        builder = builder.WithDeveloperTools().LogToTrace();
#endif
        return builder;
    }
}
