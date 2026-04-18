using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace pdfSignr.Views;

/// <summary>
/// Custom window used by the managed file dialog so that tiling WMs (Sway, i3, Hyprland, …)
/// float the dialog instead of tiling it.  On Linux/X11 we set _NET_WM_WINDOW_TYPE_DIALOG
/// after the native window is created; on other platforms this is a plain centered window.
/// </summary>
public class FileDialogWindow : Window
{
    private const string LibX11 = "libX11";
    private const int PropModeReplace = 0;

    static FileDialogWindow()
    {
        // Try common sonames in order. libX11.so.6 is what ships on most distros today, but
        // pinning it in DllImport would break on systems that ship only libX11.so (.7+, etc.).
        NativeLibrary.SetDllImportResolver(typeof(FileDialogWindow).Assembly, Resolve);

        static nint Resolve(string name, System.Reflection.Assembly asm, DllImportSearchPath? path)
        {
            if (name != LibX11 || !OperatingSystem.IsLinux()) return 0;
            foreach (var candidate in new[] { "libX11.so.6", "libX11.so" })
                if (NativeLibrary.TryLoad(candidate, asm, path, out var handle))
                    return handle;
            return 0;
        }
    }

    public FileDialogWindow()
    {
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        if (OperatingSystem.IsLinux())
            TrySetX11DialogHint();
    }

    private void TrySetX11DialogHint()
    {
        try
        {
            var platformHandle = TryGetPlatformHandle();
            if (platformHandle is null) return;

            var display = XOpenDisplay(null);
            if (display == nint.Zero) return;

            try
            {
                var wmType = XInternAtom(display, "_NET_WM_WINDOW_TYPE", false);
                var dialogAtom = XInternAtom(display, "_NET_WM_WINDOW_TYPE_DIALOG", false);
                var atomType = XInternAtom(display, "ATOM", false);

                nint[] data = [dialogAtom];
                var pin = GCHandle.Alloc(data, GCHandleType.Pinned);
                try
                {
                    XChangeProperty(display, platformHandle.Handle,
                        wmType, atomType, 32, PropModeReplace,
                        pin.AddrOfPinnedObject(), 1);
                }
                finally
                {
                    pin.Free();
                }

                XFlush(display);
            }
            finally
            {
                XCloseDisplay(display);
            }
        }
        catch
        {
            // Non-critical cosmetic hint — swallow any interop failure.
        }
    }

    [DllImport(LibX11)] private static extern nint XOpenDisplay(string? display);
    [DllImport(LibX11)] private static extern int XCloseDisplay(nint display);
    [DllImport(LibX11)] private static extern nint XInternAtom(nint display, string atomName, bool onlyIfExists);
    [DllImport(LibX11)]
    private static extern int XChangeProperty(nint display, nint window, nint property,
        nint type, int format, int mode, nint data, int nelements);
    [DllImport(LibX11)] private static extern int XFlush(nint display);
}
