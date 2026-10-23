using System.Runtime.InteropServices;
using System.Text;
using PIXORA.Core;
using SkiaSharp;

namespace PIXORA.Capture;

public sealed record MonitorInfo(IntPtr Handle, string DeviceName, RECT Bounds, RECT WorkArea, bool IsPrimary, double DpiScale)
{
    public int Width => Bounds.Width;
    public int Height => Bounds.Height;
    public string Label => $"{(IsPrimary ? "Primary" : "Display")} {Width}×{Height} ({DeviceName.TrimStart('\\', '.')})";
}

public sealed record WindowInfo(IntPtr Handle, string Title, RECT Bounds, uint ProcessId);

public class CaptureException : Exception
{
    public CaptureException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Screen and window capture built on GDI (BitBlt / PrintWindow). Coordinates are physical pixels.</summary>
public static class ScreenCapture
{
    private static readonly object InitGate = new();
    private static bool _dpiInit;

    /// <summary>Makes the process per-monitor DPI aware so coordinates are physical pixels (no-op if already set).</summary>
    public static void EnsureDpiAwareness()
    {
        lock (InitGate)
        {
            if (_dpiInit) return;
            _dpiInit = true;
            try { Native.SetProcessDpiAwarenessContext(Native.DPI_PER_MONITOR_AWARE_V2); } catch (EntryPointNotFoundException) { }
        }
    }

    public static RECT VirtualScreen
    {
        get
        {
            EnsureDpiAwareness();
            int x = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN), y = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
            return new RECT(x, y, x + Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN), y + Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN));
        }
    }

    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        EnsureDpiAwareness();
        var list = new List<MonitorInfo>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref RECT r, IntPtr _) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (Native.GetMonitorInfo(h, ref mi))
            {
                double scale = 1.0;
                if (Native.GetDpiForMonitor(h, 0, out var dx, out _) == 0 && dx > 0) scale = dx / 96.0;
                list.Add(new MonitorInfo(h, mi.szDevice, mi.rcMonitor, mi.rcWork, (mi.dwFlags & 1) != 0, scale));
            }
            return true;
        }, IntPtr.Zero);
        return list.OrderByDescending(m => m.IsPrimary).ThenBy(m => m.Bounds.Left).ToList();
    }

    public static MonitorInfo? MonitorAt(int x, int y)
    {
        var h = Native.MonitorFromPoint(new POINT { X = x, Y = y }, 2 /* nearest */);
        return GetMonitors().FirstOrDefault(m => m.Handle == h);
    }

    /// <summary>Captures an arbitrary rectangle of the virtual desktop (physical pixels).</summary>
    public static SKBitmap CaptureRect(int x, int y, int width, int height)
    {
        if (width < 1 || height < 1) throw new CaptureException("The capture area is empty.");
        EnsureDpiAwareness();
        var screen = Native.GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) throw new CaptureException("Could not access the screen. Is the session locked?");
        try
        {
            return BlitToBitmap(width, height, memDc => Native.BitBlt(memDc, 0, 0, width, height, screen, x, y, Native.SRCCOPY | Native.CAPTUREBLT));
        }
        finally { Native.ReleaseDC(IntPtr.Zero, screen); }
    }

    public static SKBitmap CaptureVirtualScreen()
    {
        var v = VirtualScreen;
        return CaptureRect(v.Left, v.Top, v.Width, v.Height);
    }

    public static SKBitmap CaptureMonitor(MonitorInfo m) => CaptureRect(m.Bounds.Left, m.Bounds.Top, m.Width, m.Height);

    /// <summary>
    /// Captures one window. Uses PrintWindow(PW_RENDERFULLCONTENT) so occluded windows still render, and falls back
    /// to copying their on-screen rectangle when the app returns a blank image (some GPU-accelerated windows do).
    /// Minimised windows cannot be captured.
    /// </summary>
    public static SKBitmap CaptureWindow(IntPtr hwnd)
    {
        EnsureDpiAwareness();
        if (!Native.GetWindowRect(hwnd, out var outer)) throw new CaptureException("That window no longer exists.");
        if (Native.IsIconic(hwnd)) throw new CaptureException("The window is minimised. Restore it and try again.");
        if (outer.Width < 1 || outer.Height < 1) throw new CaptureException("The window has no visible area.");

        RECT frame = Native.DwmGetRectAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out var f, Marshal.SizeOf<RECT>()) == 0 ? f : outer;

        SKBitmap? printed = null;
        try
        {
            printed = BlitToBitmap(outer.Width, outer.Height, memDc => Native.PrintWindow(hwnd, memDc, Native.PW_RENDERFULLCONTENT));
            if (!IsBlank(printed))
            {
                var crop = new SKRectI(
                    Math.Max(0, frame.Left - outer.Left), Math.Max(0, frame.Top - outer.Top),
                    Math.Min(outer.Width, frame.Right - outer.Left), Math.Min(outer.Height, frame.Bottom - outer.Top));
                if (crop.Width > 0 && crop.Height > 0 && (crop.Width != printed.Width || crop.Height != printed.Height))
                {
                    var cropped = Crop(printed, crop);
                    printed.Dispose();
                    printed = null;
                    return cropped;
                }
                var result = printed;
                printed = null;
                return result;
            }
        }
        finally { printed?.Dispose(); }

        Log.Warn("PrintWindow returned a blank image; falling back to a screen copy of the window rectangle.");
        return CaptureRect(frame.Left, frame.Top, frame.Width, frame.Height);
    }

    public static SKBitmap Crop(SKBitmap source, SKRectI region)
    {
        var r = SKRectI.Intersect(region, new SKRectI(0, 0, source.Width, source.Height));
        if (r.Width < 1 || r.Height < 1) throw new CaptureException("The selected area is outside the captured image.");
        var dst = new SKBitmap(new SKImageInfo(r.Width, r.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(dst);
        canvas.DrawBitmap(source, r, new SKRect(0, 0, r.Width, r.Height));
        return dst;
    }

    private static bool IsBlank(SKBitmap bmp)
    {
        var px = bmp.GetPixelSpan();
        int stride = bmp.RowBytes;
        int stepX = Math.Max(1, bmp.Width / 24), stepY = Math.Max(1, bmp.Height / 24);
        for (int y = 0; y < bmp.Height; y += stepY)
            for (int x = 0; x < bmp.Width; x += stepX)
            {
                int o = y * stride + x * 4;
                if (px[o] != 0 || px[o + 1] != 0 || px[o + 2] != 0) return false;
            }
        return true;
    }

    private static SKBitmap BlitToBitmap(int width, int height, Func<IntPtr, bool> draw)
    {
        var screen = Native.GetDC(IntPtr.Zero);
        var memDc = Native.CreateCompatibleDC(screen);
        var header = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
        var dib = Native.CreateDIBSection(screen, ref header, 0, out var bits, IntPtr.Zero, 0);
        Native.ReleaseDC(IntPtr.Zero, screen);
        if (dib == IntPtr.Zero || bits == IntPtr.Zero)
        {
            Native.DeleteDC(memDc);
            throw new CaptureException("Not enough memory for a capture of this size.");
        }
        var old = Native.SelectObject(memDc, dib);
        try
        {
            if (!draw(memDc)) throw new CaptureException("The capture failed. The screen may be locked or protected.");
            var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
            unsafe
            {
                var src = (byte*)bits;
                var dst = (byte*)bmp.GetPixels();
                int bytes = width * height * 4;
                Buffer.MemoryCopy(src, dst, bytes, bytes);
                for (int i = 3; i < bytes; i += 4) dst[i] = 0xFF; // GDI leaves alpha at 0
            }
            return bmp;
        }
        finally
        {
            Native.SelectObject(memDc, old);
            Native.DeleteObject(dib);
            Native.DeleteDC(memDc);
        }
    }

    /// <summary>Visible, titled, non-cloaked top-level windows in Z order (topmost first).</summary>
    public static IReadOnlyList<WindowInfo> GetCapturableWindows(IEnumerable<IntPtr>? exclude = null)
    {
        EnsureDpiAwareness();
        var skip = new HashSet<IntPtr>(exclude ?? Array.Empty<IntPtr>());
        var list = new List<WindowInfo>();
        Native.EnumWindows((hwnd, _) =>
        {
            if (skip.Contains(hwnd) || !Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd)) return true;
            if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            if (((long)Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE) & Native.WS_EX_TOOLWINDOW) != 0) return true;
            int len = Native.GetWindowTextLength(hwnd);
            if (len == 0) return true;
            var sb = new StringBuilder(len + 1);
            Native.GetWindowText(hwnd, sb, sb.Capacity);
            var title = sb.ToString();
            RECT r = Native.DwmGetRectAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out var f, Marshal.SizeOf<RECT>()) == 0
                ? f : (Native.GetWindowRect(hwnd, out var gr) ? gr : default);
            if (r.Width < 20 || r.Height < 20) return true;
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            list.Add(new WindowInfo(hwnd, title, r, pid));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static WindowInfo? WindowAt(IReadOnlyList<WindowInfo> windows, int x, int y) =>
        windows.FirstOrDefault(w => x >= w.Bounds.Left && x < w.Bounds.Right && y >= w.Bounds.Top && y < w.Bounds.Bottom);
}
