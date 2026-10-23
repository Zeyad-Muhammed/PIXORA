using PIXORA.Core;

namespace PIXORA.Tests;

public class SettingsTests
{
    [Fact]
    public void Defaults_are_valid()
    {
        Assert.Empty(SettingsValidator.Validate(new AppSettings()));
    }

    [Fact]
    public void Settings_persist_across_store_instances()
    {
        using var tmp = new TempDir();
        var paths = AppPaths.Under(tmp.Path);
        var first = new SettingsStore(paths);
        var s = first.Load();
        s.ScreenshotDir = Path.Combine(tmp.Path, "shots");
        s.RecordingFrameRate = 60;
        s.ScreenshotFormat = ImageFormat.WebP;
        s.Hotkeys.RegionCapture = "Ctrl+Alt+P";
        first.Save(s);

        var second = new SettingsStore(paths).Load(); // simulates an application restart
        Assert.Equal(60, second.RecordingFrameRate);
        Assert.Equal(ImageFormat.WebP, second.ScreenshotFormat);
        Assert.Equal("Ctrl+Alt+P", second.Hotkeys.RegionCapture);
        Assert.Equal(Path.Combine(tmp.Path, "shots"), second.ScreenshotDir);
    }

    [Fact]
    public void Invalid_settings_are_rejected_and_not_written()
    {
        using var tmp = new TempDir();
        var store = new SettingsStore(AppPaths.Under(tmp.Path));
        var s = store.Load();
        s.RecordingFrameRate = 17;
        s.ImageQuality = 0;
        s.ScreenshotDir = "relative\\path";
        s.ScreenshotNameTemplate = "bad{token}";
        var ex = Assert.Throws<SettingsException>(() => store.Save(s));
        Assert.True(ex.Errors.Count >= 4);
        Assert.False(File.Exists(AppPaths.Under(tmp.Path).SettingsFile));
    }

    [Fact]
    public void Duplicate_hotkeys_are_rejected()
    {
        var s = new AppSettings();
        s.Hotkeys.FullScreenCapture = s.Hotkeys.RegionCapture;
        Assert.Contains(SettingsValidator.Validate(s), e => e.Contains("unique"));
    }

    [Fact]
    public void Corrupt_settings_file_falls_back_to_defaults_and_is_preserved()
    {
        using var tmp = new TempDir();
        var paths = AppPaths.Under(tmp.Path);
        Directory.CreateDirectory(paths.SettingsDir);
        File.WriteAllText(paths.SettingsFile, "{ this is not json");
        var s = new SettingsStore(paths).Load();
        Assert.Equal(30, s.RecordingFrameRate);
        Assert.True(File.Exists(paths.SettingsFile + ".bad"));
    }

    [Fact]
    public void Restore_defaults_overwrites_changes()
    {
        using var tmp = new TempDir();
        var store = new SettingsStore(AppPaths.Under(tmp.Path));
        var s = store.Load(); s.RecordingFrameRate = 15; store.Save(s);
        store.RestoreDefaults();
        Assert.Equal(30, new SettingsStore(AppPaths.Under(tmp.Path)).Load().RecordingFrameRate);
    }

    [Fact]
    public void Probe_writable_reports_unwritable_location()
    {
        using var tmp = new TempDir();
        var file = tmp.File("iamafile");
        File.WriteAllText(file, "x");
        Assert.NotNull(SettingsValidator.ProbeWritable(Path.Combine(file, "sub")));
        Assert.Null(SettingsValidator.ProbeWritable(tmp.File("ok")));
    }
}

public class NamingAndHotkeyTests
{
    [Fact]
    public void Template_expands_tokens_and_sanitises()
    {
        var t = new DateTime(2026, 10, 23, 14, 5, 9);
        Assert.Equal("Shot_2026-10-23_14-05-09", NamingTemplate.Expand("Shot_{date}_{time}", t));
        Assert.Equal("a_b", NamingTemplate.Expand("a:b", t));
        Assert.Equal("n7", NamingTemplate.Expand("n{counter}", t, 7));
    }

    [Fact]
    public void Unique_path_never_collides()
    {
        using var tmp = new TempDir();
        var p1 = NamingTemplate.UniquePath(tmp.Path, "x", ".png");
        File.WriteAllText(p1, "1");
        var p2 = NamingTemplate.UniquePath(tmp.Path, "x", "png");
        File.WriteAllText(p2, "2");
        var p3 = NamingTemplate.UniquePath(tmp.Path, "x", ".png");
        Assert.Equal(3, new[] { p1, p2, p3 }.Distinct().Count());
        Assert.EndsWith("x (2).png", p2);
    }

    [Theory]
    [InlineData("Ctrl+Shift+S", true, (uint)'S')]
    [InlineData("alt+F9", true, 0x78u)]
    [InlineData("Win+Shift+3", true, (uint)'3')]
    [InlineData("S", false, 0u)]
    [InlineData("Ctrl+Banana", false, 0u)]
    [InlineData("Foo+S", false, 0u)]
    public void Hotkey_parser(string text, bool ok, uint vk)
    {
        Assert.Equal(ok, HotkeyParser.TryParse(text, out _, out var parsed));
        if (ok) Assert.Equal(vk, parsed);
    }

    [Fact]
    public void Empty_hotkey_means_unassigned()
    {
        Assert.True(HotkeyParser.TryParse("", out var m, out var vk));
        Assert.Equal(0u, vk);
        Assert.Equal(HotkeyModifiers.None, m);
    }
}
