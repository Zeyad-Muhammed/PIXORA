using SkiaSharp;

namespace PIXORA.Editor;

/// <summary>Composites the base image and annotations. Blur/pixelate rewrite the actual pixels.</summary>
public static class Renderer
{
    /// <summary>Renders at full resolution without cropping (used for the live editor view).</summary>
    public static SKBitmap Composite(SKBitmap baseImage, IEnumerable<Annotation> annotations, Annotation? selected = null)
    {
        var result = new SKBitmap(new SKImageInfo(baseImage.Width, baseImage.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(result);
        canvas.DrawBitmap(baseImage, 0, 0);

        foreach (var a in annotations)
        {
            switch (a)
            {
                case BlurAnnotation b: ApplyBlur(result, b); break;
                case PixelateAnnotation p: ApplyPixelate(result, p); break;
                default: a.Draw(canvas); break;
            }
        }

        if (selected != null) DrawSelection(canvas, selected);
        return result;
    }

    /// <summary>Final image for export: composite then crop.</summary>
    public static SKBitmap Flatten(EditorDocument doc)
    {
        using var full = Composite(doc.Base, doc.Annotations);
        if (doc.Crop is not { } crop) return full.Copy();
        var r = SKRectI.Intersect(crop, new SKRectI(0, 0, full.Width, full.Height));
        if (r.Width < 1 || r.Height < 1) return full.Copy();
        var cropped = new SKBitmap(new SKImageInfo(r.Width, r.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        full.ExtractSubset(cropped, r);
        // ExtractSubset shares memory with 'full'; copy so the result owns its pixels.
        var owned = cropped.Copy();
        cropped.Dispose();
        return owned;
    }

    private static SKRectI ClampRect(SKRect r, int w, int h)
    {
        var ri = new SKRectI((int)MathF.Floor(r.Left), (int)MathF.Floor(r.Top), (int)MathF.Ceiling(r.Right), (int)MathF.Ceiling(r.Bottom));
        return SKRectI.Intersect(ri, new SKRectI(0, 0, w, h));
    }

    public static void ApplyPixelate(SKBitmap bmp, PixelateAnnotation a)
    {
        var r = ClampRect(a.Rect, bmp.Width, bmp.Height);
        if (r.Width < 1 || r.Height < 1) return;
        int block = Math.Max(PixelateAnnotation.MinBlock, a.BlockSize);
        var pixels = bmp.GetPixelSpan();
        int stride = bmp.RowBytes;
        for (int by = r.Top; by < r.Bottom; by += block)
        {
            for (int bx = r.Left; bx < r.Right; bx += block)
            {
                int x1 = Math.Min(bx + block, r.Right), y1 = Math.Min(by + block, r.Bottom);
                long sb = 0, sg = 0, sr = 0, sa = 0; int n = 0;
                for (int y = by; y < y1; y++)
                    for (int x = bx; x < x1; x++)
                    {
                        int o = y * stride + x * 4;
                        sb += pixels[o]; sg += pixels[o + 1]; sr += pixels[o + 2]; sa += pixels[o + 3]; n++;
                    }
                byte ab = (byte)(sb / n), ag = (byte)(sg / n), ar = (byte)(sr / n), aa = (byte)(sa / n);
                for (int y = by; y < y1; y++)
                    for (int x = bx; x < x1; x++)
                    {
                        int o = y * stride + x * 4;
                        pixels[o] = ab; pixels[o + 1] = ag; pixels[o + 2] = ar; pixels[o + 3] = aa;
                    }
            }
        }
    }

    public static void ApplyBlur(SKBitmap bmp, BlurAnnotation a)
    {
        var r = ClampRect(a.Rect, bmp.Width, bmp.Height);
        if (r.Width < 1 || r.Height < 1) return;
        float sigma = Math.Max(BlurAnnotation.MinStrength, a.Strength);
        int pad = (int)MathF.Ceiling(sigma * 3);
        var src = SKRectI.Intersect(new SKRectI(r.Left - pad, r.Top - pad, r.Right + pad, r.Bottom + pad), new SKRectI(0, 0, bmp.Width, bmp.Height));

        using var sub = new SKBitmap(new SKImageInfo(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (!bmp.ExtractSubset(sub, src)) return;
        using var subCopy = sub.Copy();

        using var surface = SKSurface.Create(new SKImageInfo(r.Width, r.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var c = surface.Canvas;
        c.Clear(SKColors.Transparent);
        using var paint = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp) };
        c.DrawBitmap(subCopy, src.Left - r.Left, src.Top - r.Top, paint);
        c.Flush();

        using var blurred = new SKBitmap(new SKImageInfo(r.Width, r.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var pm = blurred.PeekPixels();
        if (!surface.ReadPixels(blurred.Info, blurred.GetPixels(), blurred.RowBytes, 0, 0)) return;

        var dst = bmp.GetPixelSpan();
        var srcPx = blurred.GetPixelSpan();
        for (int y = 0; y < r.Height; y++)
            srcPx.Slice(y * blurred.RowBytes, r.Width * 4).CopyTo(dst.Slice((r.Top + y) * bmp.RowBytes + r.Left * 4, r.Width * 4));
    }

    private static void DrawSelection(SKCanvas canvas, Annotation a)
    {
        var accent = new SKColor(0x4C, 0x9A, 0xFF);
        var b = a.Bounds;
        b.Inflate(4, 4);
        using var outline = new SKPaint
        {
            Color = accent, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f,
            PathEffect = SKPathEffect.CreateDash(new[] { 6f, 4f }, 0),
        };
        if (a is not LineBase) canvas.DrawRect(b, outline);
        using var fill = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Fill };
        using var edge = new SKPaint { Color = accent, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2f };
        foreach (var h in a.Handles)
        {
            canvas.DrawCircle(h, 5f, fill);
            canvas.DrawCircle(h, 5f, edge);
        }
    }
}
