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
    public FileDialogWindow()
    {
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        // The native X11 window is created in the base Window() constructor,
        // so the handle is available here — before the window is mapped.
        // Setting the type now means the WM sees it on the initial MapRequest.
        if (OperatingSystem.IsLinux())
            TrySetX11DialogHint();
    }

    private void TrySetX11DialogHint()
    {
        try
        {
            var platformHandle = TryGetPlatformHandle();
            if (platformHandle is null)
                return;

            var display = XOpenDisplay(null);
            if (display == nint.Zero)
                return;

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

    private const int PropModeReplace = 0;

    [DllImport("libX11.so.6")]
    private static extern nint XOpenDisplay(string? display);

    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(nint display);

    [DllImport("libX11.so.6")]
    private static extern nint XInternAtom(nint display, string atomName, bool onlyIfExists);

    [DllImport("libX11.so.6")]
    private static extern int XChangeProperty(nint display, nint window, nint property,
        nint type, int format, int mode, nint data, int nelements);

    [DllImport("libX11.so.6")]
    private static extern int XFlush(nint display);
}
