using PIXORA.Core;
using PIXORA.Editor;
using SkiaSharp;

namespace PIXORA.Tests;

public class EditorTests
{
    [Fact]
    public void Pixelate_destroys_detail_and_flattens_blocks()
    {
        using var src = TestImages.Noise(200, 120);
        using var doc = new EditorDocument(src.Copy());
        var region = new SKRectI(40, 30, 160, 90);
        Assert.True(TestImages.Roughness(src, region) > 60); // sanity: the source really is detailed
        doc.Add(new PixelateAnnotation { BlockSize = 20 }.At(40, 30, 160, 90));

        using var flat = Renderer.Flatten(doc);
        // Every 20x20 block inside the region must be a single colour: the original pixels are gone.
        for (int by = 30; by < 90; by += 20)
            for (int bx = 40; bx < 160; bx += 20)
            {
                var c = flat.GetPixel(bx, by);
                for (int y = by; y < by + 20; y++)
                    for (int x = bx; x < bx + 20; x++)
                        Assert.Equal(c, flat.GetPixel(x, y));
            }
        // Outside the region nothing changed.
        Assert.True(TestImages.SamePixels(src, flat, new SKRectI(0, 0, 200, 30)));
        Assert.True(TestImages.SamePixels(src, flat, new SKRectI(0, 90, 200, 120)));
        Assert.True(TestImages.SamePixels(src, flat, new SKRectI(0, 0, 40, 120)));
    }

    [Fact]
    public void Blur_conceals_detail_in_the_exported_file_not_just_on_screen()
    {
        using var tmp = new TempDir();
        using var src = TestImages.Noise(240, 160);
        using var doc = new EditorDocument(src.Copy());
        var region = new SKRectI(60, 40, 180, 120);
        doc.Add(new BlurAnnotation { Strength = 16 }.At(60, 40, 180, 120));

        var path = tmp.File("blurred.png");
        ImageExporter.SaveDocument(doc, path, ImageFormat.Png);

        using var exported = SKBitmap.Decode(path);
        var inner = new SKRectI(80, 60, 160, 100);
        double before = TestImages.Roughness(src, inner);
        double after = TestImages.Roughness(exported, inner);
        Assert.True(before > 60, $"source roughness {before}");
        Assert.True(after < before * 0.1, $"blurred roughness {after} should be far below {before}");
        // Outside the blurred rectangle the file is byte-for-byte the original pixels.
        Assert.True(TestImages.SamePixels(src, exported, new SKRectI(0, 0, 240, 40)));
        Assert.False(TestImages.SamePixels(src, exported, inner));
    }

    [Fact]
    public void Blur_strength_has_a_floor_so_it_cannot_be_set_to_a_no_op()
    {
        using var src = TestImages.Noise(120, 80);
        using var doc = new EditorDocument(src.Copy());
        doc.Add(new BlurAnnotation { Strength = 0 }.At(10, 10, 110, 70));
        using var flat = Renderer.Flatten(doc);
        Assert.True(TestImages.Roughness(flat, new SKRectI(30, 25, 90, 55)) < TestImages.Roughness(src, new SKRectI(30, 25, 90, 55)) * 0.35);
    }

    [Fact]
    public void Annotations_are_baked_into_export_with_their_colour()
    {
        using var tmp = new TempDir();
        using var doc = new EditorDocument(TestImages.Solid(100, 100, SKColors.White));
        doc.Add(new RectangleAnnotation { Color = new SKColor(200, 20, 30), StrokeWidth = 6, CornerRadius = 0 }.At(20, 20, 80, 80));
        doc.Add(new HighlightAnnotation { Color = SKColors.Yellow, Opacity = 1f }.At(30, 30, 50, 50));
        doc.Add(new StepAnnotation { Number = 3, Center = new SKPoint(70, 30), Radius = 12, Color = new SKColor(10, 90, 200) });
        var p = tmp.File("a.png");
        ImageExporter.SaveDocument(doc, p, ImageFormat.Png);
        using var bmp = SKBitmap.Decode(p);
        Assert.Equal(new SKColor(200, 20, 30), bmp.GetPixel(20, 50));       // left edge of stroke
        Assert.Equal(SKColors.Yellow.WithAlpha(255), bmp.GetPixel(40, 40)); // highlight
        Assert.Equal(SKColors.White, bmp.GetPixel(5, 5));                   // untouched background
        Assert.Equal(new SKColor(10, 90, 200), bmp.GetPixel(70, 21));       // step marker fill (above the digit)
    }

    [Fact]
    public void Arrow_line_ellipse_and_text_draw_something()
    {
        using var doc = new EditorDocument(TestImages.Solid(200, 200, SKColors.White));
        doc.Add(new ArrowAnnotation { Start = new SKPoint(10, 10), End = new SKPoint(100, 10), Color = SKColors.Black });
        doc.Add(new LineAnnotation { Start = new SKPoint(10, 40), End = new SKPoint(100, 40), Color = SKColors.Black });
        doc.Add(new EllipseAnnotation { Color = SKColors.Black }.At(20, 60, 100, 120));
        doc.Add(new TextAnnotation { Text = "HELLO", Position = new SKPoint(20, 140), Color = SKColors.Black, FontSize = 32 });
        using var flat = Renderer.Flatten(doc);
        bool Dark(SKRectI r) { for (int y = r.Top; y < r.Bottom; y++) for (int x = r.Left; x < r.Right; x++) if (flat.GetPixel(x, y).Red < 100) return true; return false; }
        Assert.True(Dark(new SKRectI(10, 5, 110, 15)));
        Assert.True(Dark(new SKRectI(10, 35, 110, 45)));
        Assert.True(Dark(new SKRectI(15, 55, 105, 125)));
        Assert.True(Dark(new SKRectI(18, 138, 130, 180)));
    }

    [Fact]
    public void Crop_changes_exported_size_and_is_non_destructive()
    {
        using var tmp = new TempDir();
        using var doc = new EditorDocument(TestImages.Noise(300, 200));
        doc.SetCrop(new SKRectI(50, 40, 250, 140));
        Assert.Equal(300, doc.Base.Width); // base untouched
        var p = tmp.File("c.png");
        ImageExporter.SaveDocument(doc, p, ImageFormat.Png);
        using var bmp = SKBitmap.Decode(p);
        Assert.Equal((200, 100), (bmp.Width, bmp.Height));
        Assert.Equal(doc.Base.GetPixel(50, 40), bmp.GetPixel(0, 0));
        doc.SetCrop(null);
        Assert.Equal(300, doc.Width);
    }

    [Fact]
    public void Undo_redo_restores_annotations_and_crop()
    {
        using var doc = new EditorDocument(TestImages.Solid(100, 100, SKColors.White));
        Assert.False(doc.CanUndo);
        doc.Add(new LineAnnotation { Start = new SKPoint(0, 0), End = new SKPoint(10, 10) });
        doc.Add(new LineAnnotation { Start = new SKPoint(0, 5), End = new SKPoint(10, 15) });
        doc.SetCrop(new SKRectI(0, 0, 50, 50));
        Assert.Equal(2, doc.Annotations.Count);
        doc.Undo(); Assert.Null(doc.Crop);
        doc.Undo(); Assert.Single(doc.Annotations);
        doc.Redo(); Assert.Equal(2, doc.Annotations.Count);
        doc.Redo(); Assert.NotNull(doc.Crop);
        Assert.False(doc.CanRedo);
        doc.Undo();
        doc.Add(new LineAnnotation()); // a new edit clears redo
        Assert.False(doc.CanRedo);
    }

    [Fact]
    public void Moving_an_annotation_is_undoable_and_hit_testing_finds_it()
    {
        using var doc = new EditorDocument(TestImages.Solid(200, 200, SKColors.White));
        var r = new RectangleAnnotation().At(50, 50, 120, 120);
        doc.Add(r);
        Assert.Same(r, doc.HitTest(new SKPoint(50, 80), 4));  // on the outline
        Assert.Null(doc.HitTest(new SKPoint(85, 85), 4));     // inside, not on outline
        Assert.Null(doc.HitTest(new SKPoint(180, 180), 4));

        doc.Checkpoint();
        doc.Annotations[0].Translate(10, 20);
        Assert.Equal(60, ((BoxAnnotation)doc.Annotations[0]).Left);
        doc.Undo();
        Assert.Equal(50, ((BoxAnnotation)doc.Annotations[0]).Left);
    }

    [Fact]
    public void Resize_handles_move_corners()
    {
        var r = new RectangleAnnotation().At(10, 10, 50, 50);
        r.MoveHandle(2, new SKPoint(80, 90));
        Assert.Equal(new SKRect(10, 10, 80, 90), r.Rect);
        var a = new ArrowAnnotation { Start = new SKPoint(0, 0), End = new SKPoint(5, 5) };
        a.MoveHandle(1, new SKPoint(50, 60));
        Assert.Equal(new SKPoint(50, 60), a.End);
    }

    [Fact]
    public void Step_numbers_increment()
    {
        using var doc = new EditorDocument(TestImages.Solid(50, 50, SKColors.White));
        Assert.Equal(1, doc.NextStepNumber());
        doc.Add(new StepAnnotation { Number = 1 });
        doc.Add(new StepAnnotation { Number = 2 });
        Assert.Equal(3, doc.NextStepNumber());
    }

    [Theory]
    [InlineData(ImageFormat.Png)]
    [InlineData(ImageFormat.Jpeg)]
    [InlineData(ImageFormat.WebP)]
    public void Export_formats_round_trip(ImageFormat fmt)
    {
        using var tmp = new TempDir();
        using var src = TestImages.Solid(64, 48, new SKColor(30, 140, 220));
        var p = tmp.File("x" + NamingTemplate.Extension(fmt));
        ImageExporter.Save(src, p, fmt, 95);
        using var back = SKBitmap.Decode(p);
        Assert.NotNull(back);
        Assert.Equal((64, 48), (back.Width, back.Height));
        var c = back.GetPixel(32, 24);
        Assert.InRange(c.Red, 20, 45); Assert.InRange(c.Green, 125, 155); Assert.InRange(c.Blue, 205, 235);
        Assert.Empty(Directory.GetFiles(tmp.Path, "*.tmp")); // atomic write leaves no temp file
    }

    [Fact]
    public void Open_rejects_non_images_and_opens_real_ones()
    {
        using var tmp = new TempDir();
        var bad = tmp.File("bad.png"); File.WriteAllText(bad, "not an image");
        Assert.Throws<InvalidDataException>(() => EditorDocument.Open(bad));
        var good = tmp.File("good.png");
        ImageExporter.Save(TestImages.Solid(20, 20, SKColors.Red), good, ImageFormat.Png);
        using var d = EditorDocument.Open(good);
        Assert.Equal(20, d.Base.Width);
        Assert.Equal(good, d.FilePath);
        Assert.False(d.IsDirty);
        d.Add(new LineAnnotation());
        Assert.True(d.IsDirty);
        d.MarkSaved();
        Assert.False(d.IsDirty);
    }

    [Fact]
    public void Failed_save_does_not_destroy_existing_file()
    {
        using var tmp = new TempDir();
        var p = tmp.File("keep.png");
        ImageExporter.Save(TestImages.Solid(10, 10, SKColors.Red), p, ImageFormat.Png);
        var before = File.ReadAllBytes(p);
        // Directory in place of the file => the final move must fail.
        var blocked = tmp.File("blocked.png"); Directory.CreateDirectory(blocked);
        Assert.ThrowsAny<Exception>(() => ImageExporter.Save(TestImages.Solid(10, 10, SKColors.Blue), blocked, ImageFormat.Png));
        Assert.Equal(before, File.ReadAllBytes(p));
        Assert.Empty(Directory.GetFiles(tmp.Path, "*.tmp"));
    }
}
