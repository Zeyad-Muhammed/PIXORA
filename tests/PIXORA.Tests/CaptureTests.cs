using PIXORA.Capture;
using PIXORA.Core;
using SkiaSharp;

namespace PIXORA.Tests;

/// <summary>These need an interactive desktop session; on a headless runner (no monitors) they pass vacuously and say so.</summary>
[Trait("Category", "Desktop")]
public class CaptureTests
{
    private static bool HasDisplay => ScreenCapture.GetMonitors().Count > 0;

    [Fact]
    public void Monitors_are_enumerated_with_valid_geometry()
    {
        if (!HasDisplay) return;
        var mons = ScreenCapture.GetMonitors();
        Assert.Single(mons, m => m.IsPrimary);
        Assert.All(mons, m => { Assert.True(m.Width > 0 && m.Height > 0); Assert.True(m.DpiScale >= 1.0); });
        var v = ScreenCapture.VirtualScreen;
        Assert.All(mons, m => Assert.True(m.Bounds.Left >= v.Left && m.Bounds.Right <= v.Right));
    }

    [Fact]
    public void Primary_monitor_capture_has_exact_pixel_size_and_is_opaque()
    {
        if (!HasDisplay) return;
        var m = ScreenCapture.GetMonitors().First(x => x.IsPrimary);
        using var bmp = ScreenCapture.CaptureMonitor(m);
        Assert.Equal((m.Width, m.Height), (bmp.Width, bmp.Height));
        Assert.Equal(0xFF, bmp.GetPixel(bmp.Width / 2, bmp.Height / 2).Alpha);
    }

    [Fact]
    public void Virtual_screen_capture_matches_virtual_desktop_size()
    {
        if (!HasDisplay) return;
        var v = ScreenCapture.VirtualScreen;
        using var bmp = ScreenCapture.CaptureVirtualScreen();
        Assert.Equal((v.Width, v.Height), (bmp.Width, bmp.Height));
    }

    [Fact]
    public void Region_capture_equals_the_same_area_cropped_from_a_full_capture()
    {
        if (!HasDisplay) return;
        var m = ScreenCapture.GetMonitors().First(x => x.IsPrimary);
        using var full = ScreenCapture.CaptureMonitor(m);
        using var region = ScreenCapture.CaptureRect(m.Bounds.Left + 10, m.Bounds.Top + 10, 64, 48);
        Assert.Equal((64, 48), (region.Width, region.Height));
        using var cropped = ScreenCapture.Crop(full, new SKRectI(10, 10, 74, 58));
        // The desktop may change between two grabs (clock, cursor blink); require near-total agreement.
        int same = 0;
        for (int y = 0; y < 48; y++) for (int x = 0; x < 64; x++) if (region.GetPixel(x, y) == cropped.GetPixel(x, y)) same++;
        Assert.True(same > 64 * 48 * 0.9, $"only {same} of {64 * 48} pixels matched");
    }

    [Fact]
    public void Capturing_an_empty_area_throws_a_friendly_error()
    {
        Assert.Throws<CaptureException>(() => ScreenCapture.CaptureRect(0, 0, 0, 10));
        Assert.Throws<CaptureException>(() => ScreenCapture.Crop(TestImages.Solid(10, 10, SKColors.Red), new SKRectI(50, 50, 60, 60)));
    }

    [Fact]
    public void Invalid_window_handle_reports_an_error_instead_of_crashing()
    {
        Assert.Throws<CaptureException>(() => ScreenCapture.CaptureWindow(new IntPtr(0x7FFFFFF)));
    }

    [Fact]
    public void Window_enumeration_returns_sane_entries_and_capture_works_for_one()
    {
        if (!HasDisplay) return;
        var windows = ScreenCapture.GetCapturableWindows();
        Assert.All(windows, w => { Assert.False(string.IsNullOrEmpty(w.Title)); Assert.True(w.Bounds.Width >= 20); });
        var target = windows.FirstOrDefault();
        if (target == null) return; // nothing on screen
        using var bmp = ScreenCapture.CaptureWindow(target.Handle);
        Assert.True(bmp.Width > 0 && bmp.Height > 0);
    }

    [Fact]
    public void Clipboard_image_round_trip()
    {
        if (!HasDisplay) return;
        using var src = TestImages.Noise(37, 23, seed: 99);
        ClipboardService.SetImage(src);
        Assert.True(ClipboardService.HasImage());
        using var back = ClipboardService.GetImage();
        Assert.NotNull(back);
        Assert.Equal((37, 23), (back!.Width, back.Height));
        Assert.True(TestImages.SamePixels(src, back, new SKRectI(0, 0, 37, 23)));
    }

    [Fact]
    public void Clipboard_file_list_round_trip()
    {
        if (!HasDisplay) return;
        using var tmp = new TempDir();
        var f1 = tmp.File("a.png"); var f2 = tmp.File("b b.png");
        File.WriteAllText(f1, "1"); File.WriteAllText(f2, "2");
        ClipboardService.SetFiles(new[] { f1, f2 });
        Assert.Equal(new[] { f1, f2 }, ClipboardService.GetFiles());
    }

    [Fact]
    public void Screenshot_saver_uses_template_format_and_never_overwrites()
    {
        using var tmp = new TempDir();
        var s = new AppSettings { ScreenshotDir = tmp.File("out"), ScreenshotNameTemplate = "Shot_{date}", ScreenshotFormat = ImageFormat.Jpeg, ImageQuality = 80 };
        var now = new DateTime(2026, 1, 2, 3, 4, 5);
        using var img = TestImages.Solid(50, 40, SKColors.CornflowerBlue);
        var p1 = ScreenshotSaver.Save(img, s, now);
        var p2 = ScreenshotSaver.Save(img, s, now);
        Assert.Equal("Shot_2026-01-02.jpg", Path.GetFileName(p1));
        Assert.Equal("Shot_2026-01-02 (2).jpg", Path.GetFileName(p2));
        using var back = SKBitmap.Decode(p1);
        Assert.Equal((50, 40), (back.Width, back.Height));
    }

    [Fact]
    public void Screenshot_saver_reports_unwritable_folder()
    {
        using var tmp = new TempDir();
        var blocker = tmp.File("file"); File.WriteAllText(blocker, "x");
        var s = new AppSettings { ScreenshotDir = Path.Combine(blocker, "sub") };
        using var img = TestImages.Solid(5, 5, SKColors.Red);
        Assert.Throws<CaptureException>(() => ScreenshotSaver.Save(img, s));
    }
}
