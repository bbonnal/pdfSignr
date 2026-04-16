using Avalonia;

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
            .UsePlatformDetect();
#if DEBUG
        builder = builder.WithDeveloperTools().LogToTrace();
#endif
        return builder;
    }
}
