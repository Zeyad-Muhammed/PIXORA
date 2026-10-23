using System.Runtime.InteropServices;
using System.Text;
using PIXORA.Editor;
using PIXORA.Core;
using SkiaSharp;

namespace PIXORA.Capture;

/// <summary>Win32 clipboard access for images (DIB + PNG) and file lists. Thread-agnostic; retries when another app holds the clipboard.</summary>
public static class ClipboardService
{
    private static readonly uint PngFormat = Native.RegisterClipboardFormat("PNG");

    public static void SetImage(SKBitmap bitmap)
    {
        // Pre-build every payload before touching the clipboard so it is held as briefly as possible.
        var dib = BuildDib(bitmap);
        var png = ImageExporter.Encode(bitmap, ImageFormat.Png);
        var hDib = ToGlobal(dib);
        var hPng = ToGlobal(png);
        bool ok = false;
        try
        {
            WithClipboard(() =>
            {
                Native.EmptyClipboard();
                if (Native.SetClipboardData(Native.CF_DIB, hDib) == IntPtr.Zero) throw new CaptureException("Could not place the image on the clipboard.");
                hDib = IntPtr.Zero; // ownership transferred
                if (PngFormat != 0 && Native.SetClipboardData(PngFormat, hPng) != IntPtr.Zero) hPng = IntPtr.Zero;
                ok = true;
            });
        }
        finally
        {
            if (hDib != IntPtr.Zero) Native.GlobalFree(hDib);
            if (hPng != IntPtr.Zero) Native.GlobalFree(hPng);
        }
        if (!ok) throw new CaptureException("Could not place the image on the clipboard.");
    }

    public static SKBitmap? GetImage()
    {
        SKBitmap? result = null;
        WithClipboard(() =>
        {
            if (PngFormat != 0 && Native.IsClipboardFormatAvailable(PngFormat))
            {
                var h = Native.GetClipboardData(PngFormat);
                if (h != IntPtr.Zero)
                {
                    var bytes = ReadGlobal(h);
                    result = SKBitmap.Decode(bytes);
                    if (result != null) return;
                }
            }
            if (Native.IsClipboardFormatAvailable(Native.CF_DIB))
            {
                var h = Native.GetClipboardData(Native.CF_DIB);
                if (h != IntPtr.Zero) result = ParseDib(ReadGlobal(h));
            }
        });
        return result;
    }

    public static bool HasImage()
    {
        bool has = false;
        WithClipboard(() => has = Native.IsClipboardFormatAvailable(Native.CF_DIB) || (PngFormat != 0 && Native.IsClipboardFormatAvailable(PngFormat)));
        return has;
    }

    /// <summary>Puts files on the clipboard so they can be pasted in Explorer.</summary>
    public static void SetFiles(IEnumerable<string> paths)
    {
        var joined = string.Join('\0', paths.Select(Path.GetFullPath)) + "\0\0";
        var chars = Encoding.Unicode.GetBytes(joined);
        int headerSize = Marshal.SizeOf<DROPFILES>();
        var buf = new byte[headerSize + chars.Length];
        MemoryMarshal.Write(buf, new DROPFILES { pFiles = headerSize, fWide = 1 });
        chars.CopyTo(buf, headerSize);
        var h = ToGlobal(buf);
        try
        {
            WithClipboard(() =>
            {
                Native.EmptyClipboard();
                if (Native.SetClipboardData(Native.CF_HDROP, h) == IntPtr.Zero) throw new CaptureException("Could not copy the file to the clipboard.");
                h = IntPtr.Zero;
            });
        }
        finally { if (h != IntPtr.Zero) Native.GlobalFree(h); }
    }

    public static IReadOnlyList<string> GetFiles()
    {
        var files = new List<string>();
        WithClipboard(() =>
        {
            if (!Native.IsClipboardFormatAvailable(Native.CF_HDROP)) return;
            var h = Native.GetClipboardData(Native.CF_HDROP);
            if (h == IntPtr.Zero) return;
            var bytes = ReadGlobal(h);
            int offset = BitConverter.ToInt32(bytes, 0);
            var text = Encoding.Unicode.GetString(bytes, offset, bytes.Length - offset);
            files.AddRange(text.Split('\0', StringSplitOptions.RemoveEmptyEntries));
        });
        return files;
    }

    private static void WithClipboard(Action action)
    {
        for (int attempt = 0; ; attempt++)
        {
            if (Native.OpenClipboard(IntPtr.Zero)) break;
            if (attempt >= 20) throw new CaptureException("The clipboard is in use by another application. Try again.");
            Thread.Sleep(25);
        }
        try { action(); }
        finally { Native.CloseClipboard(); }
    }

    private static byte[] BuildDib(SKBitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height, stride = w * 4;
        var header = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = h, biPlanes = 1, biBitCount = 32, biSizeImage = stride * h };
        var buf = new byte[header.biSize + stride * h];
        MemoryMarshal.Write(buf, header);
        var src = bmp.GetPixelSpan();
        for (int y = 0; y < h; y++) // DIBs are bottom-up
            src.Slice(y * bmp.RowBytes, stride).CopyTo(buf.AsSpan(header.biSize + (h - 1 - y) * stride, stride));
        return buf;
    }

    private static SKBitmap? ParseDib(byte[] data)
    {
        var header = MemoryMarshal.Read<BITMAPINFOHEADER>(data);
        if (header.biBitCount != 32 && header.biBitCount != 24) return null;
        int w = header.biWidth, h = Math.Abs(header.biHeight);
        bool bottomUp = header.biHeight > 0;
        int bpp = header.biBitCount / 8;
        int stride = (w * bpp + 3) & ~3;
        int offset = header.biSize + (header.biCompression == 3 ? 12 : 0);
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
        var dst = bmp.GetPixelSpan();
        for (int y = 0; y < h; y++)
        {
            int sy = bottomUp ? h - 1 - y : y;
            for (int x = 0; x < w; x++)
            {
                int so = offset + sy * stride + x * bpp, d = y * bmp.RowBytes + x * 4;
                dst[d] = data[so]; dst[d + 1] = data[so + 1]; dst[d + 2] = data[so + 2]; dst[d + 3] = 0xFF;
            }
        }
        return bmp;
    }

    private static IntPtr ToGlobal(byte[] data)
    {
        var h = Native.GlobalAlloc(Native.GMEM_MOVEABLE, (UIntPtr)data.Length);
        if (h == IntPtr.Zero) throw new CaptureException("Out of memory while preparing the clipboard.");
        var p = Native.GlobalLock(h);
        Marshal.Copy(data, 0, p, data.Length);
        Native.GlobalUnlock(h);
        return h;
    }

    private static byte[] ReadGlobal(IntPtr h)
    {
        var size = (int)Native.GlobalSize(h);
        var p = Native.GlobalLock(h);
        try
        {
            var buf = new byte[size];
            Marshal.Copy(p, buf, 0, size);
            return buf;
        }
        finally { Native.GlobalUnlock(h); }
    }
}
