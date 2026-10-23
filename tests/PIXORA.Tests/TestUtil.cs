using SkiaSharp;

namespace PIXORA.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pixora-tests-" + Guid.NewGuid().ToString("N")[..10]);

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public static class TestImages
{
    /// <summary>High-frequency pseudo-random noise: stands in for sensitive text/detail that must be concealed.</summary>
    public static SKBitmap Noise(int w, int h, int seed = 7)
    {
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        var rnd = new Random(seed);
        var px = bmp.GetPixelSpan();
        for (int i = 0; i < px.Length; i += 4)
        {
            byte v = (byte)rnd.Next(256);
            px[i] = v; px[i + 1] = (byte)(255 - v); px[i + 2] = (byte)(v ^ 0x5A); px[i + 3] = 255;
        }
        return bmp;
    }

    public static SKBitmap Solid(int w, int h, SKColor c)
    {
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        bmp.Erase(c);
        return bmp;
    }

    /// <summary>Mean absolute difference between neighbouring pixels inside a region: ~0 when flat, large for noise.</summary>
    public static double Roughness(SKBitmap bmp, SKRectI r)
    {
        double sum = 0; long n = 0;
        for (int y = r.Top; y < r.Bottom; y++)
            for (int x = r.Left; x < r.Right - 1; x++)
            {
                var a = bmp.GetPixel(x, y); var b = bmp.GetPixel(x + 1, y);
                sum += Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue);
                n += 3;
            }
        return sum / n;
    }

    public static bool SamePixels(SKBitmap a, SKBitmap b, SKRectI r)
    {
        for (int y = r.Top; y < r.Bottom; y++)
            for (int x = r.Left; x < r.Right; x++)
                if (a.GetPixel(x, y) != b.GetPixel(x, y)) return false;
        return true;
    }
}
