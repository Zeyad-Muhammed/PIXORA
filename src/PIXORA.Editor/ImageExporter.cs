using PIXORA.Core;
using SkiaSharp;

namespace PIXORA.Editor;

public static class ImageExporter
{
    public static byte[] Encode(SKBitmap bitmap, ImageFormat format, int quality = 90)
    {
        quality = Math.Clamp(quality, 1, 100);
        SKBitmap source = bitmap;
        SKBitmap? flat = null;
        try
        {
            if (format == ImageFormat.Jpeg)
            {
                // JPEG has no alpha; flatten onto white so transparent areas do not turn black.
                flat = new SKBitmap(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
                using var c = new SKCanvas(flat);
                c.Clear(SKColors.White);
                c.DrawBitmap(bitmap, 0, 0);
                source = flat;
            }
            using var image = SKImage.FromBitmap(source);
            var fmt = format switch
            {
                ImageFormat.Jpeg => SKEncodedImageFormat.Jpeg,
                ImageFormat.WebP => SKEncodedImageFormat.Webp,
                _ => SKEncodedImageFormat.Png,
            };
            using var data = image.Encode(fmt, format == ImageFormat.Png ? 100 : quality)
                ?? throw new NotSupportedException($"This system cannot encode {format}.");
            return data.ToArray();
        }
        finally { flat?.Dispose(); }
    }

    /// <summary>Writes atomically (temp file then move) so a failed write never leaves a truncated image.</summary>
    public static void Save(SKBitmap bitmap, string path, ImageFormat format, int quality = 90)
    {
        var bytes = Encode(bitmap, format, quality);
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>Renders the document (annotations + crop) and saves it. Marks the document clean when saving to its own path.</summary>
    public static void SaveDocument(EditorDocument doc, string path, ImageFormat format, int quality = 90)
    {
        using var flat = Renderer.Flatten(doc);
        Save(flat, path, format, quality);
    }
}
