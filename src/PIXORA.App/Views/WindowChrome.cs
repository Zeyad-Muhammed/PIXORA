using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using PIXORA.Capture;
using Windows.Graphics;

namespace PIXORA.App.Views;

/// <summary>Helpers for the borderless utility windows (overlay, countdown, recording HUD).</summary>
internal static class WindowChrome
{
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    public static IntPtr Handle(Window w) => WinRT.Interop.WindowNative.GetWindowHandle(w);

    /// <summary>Removes chrome, keeps the window on top and out of Alt+Tab. Size is in device-independent pixels.</summary>
    public static void MakeFloating(Window w, MonitorInfo monitor, double widthDip, double heightDip, double topDip)
    {
        var aw = w.AppWindow;
        if (aw.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(false, false);
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.IsAlwaysOnTop = true;
        }
        aw.IsShownInSwitchers = false;
        int wpx = (int)Math.Round(widthDip * monitor.DpiScale), hpx = (int)Math.Round(heightDip * monitor.DpiScale);
        int x = monitor.WorkArea.Left + (monitor.WorkArea.Width - wpx) / 2;
        int y = topDip < 0 ? monitor.WorkArea.Top + (monitor.WorkArea.Height - hpx) / 2 : monitor.WorkArea.Top + (int)(topDip * monitor.DpiScale);
        aw.MoveAndResize(new RectInt32(x, y, wpx, hpx));
    }

    /// <summary>Covers one monitor completely (used by the selection overlay).</summary>
    public static void CoverMonitor(Window w, MonitorInfo monitor)
    {
        var aw = w.AppWindow;
        aw.IsShownInSwitchers = false;
        aw.MoveAndResize(new RectInt32(monitor.Bounds.Left, monitor.Bounds.Top, monitor.Width, monitor.Height));
        aw.SetPresenter(AppWindowPresenterKind.FullScreen);
    }

    /// <summary>Keeps PIXORA's own HUD/countdown out of screenshots and recordings (Windows 10 2004+; ignored elsewhere).</summary>
    public static void ExcludeFromCapture(Window w)
    {
        try { SetWindowDisplayAffinity(Handle(w), WDA_EXCLUDEFROMCAPTURE); } catch (EntryPointNotFoundException) { }
    }
}
