using PIXORA.Core;
using PIXORA.Editor;
using PIXORA.Library;
using SkiaSharp;

namespace PIXORA.Tests;

public class LibraryTests
{
    private sealed class FakeProbe : IMediaProbe
    {
        public Task<MediaProbeResult?> ProbeAsync(string path, MediaKind kind, CancellationToken ct = default) =>
            Task.FromResult<MediaProbeResult?>(new MediaProbeResult(kind == MediaKind.Screenshot ? 100 : 1920, kind == MediaKind.Screenshot ? 50 : 1080, kind == MediaKind.Recording ? 12.5 : 0));
    }

    private static string MakePng(string dir, string name, int w = 40, int h = 30)
    {
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, name);
        ImageExporter.Save(TestImages.Noise(w, h), p, ImageFormat.Png);
        return p;
    }

    private static string MakeVideoStub(string dir, string name, int bytes = 2048)
    {
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, name);
        File.WriteAllBytes(p, new byte[bytes]);
        return p;
    }

    [Fact]
    public async Task Scan_indexes_existing_files_and_persists_across_instances()
    {
        using var tmp = new TempDir();
        MakePng(tmp.File("shots"), "a.png"); MakePng(tmp.File("shots"), "b.jpg.png");
        MakeVideoStub(tmp.File("vids"), "r.mp4");
        File.WriteAllText(Path.Combine(tmp.File("shots"), "notes.txt"), "ignore me");

        var db = tmp.File("lib.db");
        var store = new LibraryStore(db);
        var summary = await store.ScanAsync(new[] { tmp.File("shots") }, new[] { tmp.File("vids") }, new FakeProbe());
        Assert.Equal(3, summary.Added);

        var reopened = new LibraryStore(db); // restart
        Assert.Equal(3, reopened.Query().Count);
        var video = reopened.Query(LibraryCategory.Recordings).Single();
        Assert.Equal(12.5, video.DurationSeconds);
        Assert.Equal(1920, video.Width);
        Assert.Equal(2, reopened.Query(LibraryCategory.Screenshots).Count);
    }

    [Fact]
    public async Task Rescan_is_idempotent()
    {
        using var tmp = new TempDir();
        MakePng(tmp.File("s"), "a.png");
        var store = new LibraryStore(tmp.File("l.db"));
        await store.ScanAsync(new[] { tmp.File("s") }, Array.Empty<string>(), new FakeProbe());
        var again = await store.ScanAsync(new[] { tmp.File("s") }, Array.Empty<string>(), new FakeProbe());
        Assert.Equal(0, again.Added);
        Assert.Single(store.Query());
    }

    [Fact]
    public async Task Missing_files_are_flagged_not_deleted_and_recover_when_restored()
    {
        using var tmp = new TempDir();
        var png = MakePng(tmp.File("s"), "gone.png");
        var store = new LibraryStore(tmp.File("l.db"));
        await store.ScanAsync(new[] { tmp.File("s") }, Array.Empty<string>(), new FakeProbe());

        var moved = png + ".moved";
        File.Move(png, moved);
        var s1 = await store.ScanAsync(new[] { tmp.File("s") }, Array.Empty<string>(), new FakeProbe());
        Assert.Equal(1, s1.MarkedMissing);
        var item = store.Query().Single();
        Assert.True(item.IsMissing);

        File.Move(moved, png);
        await store.ScanAsync(new[] { tmp.File("s") }, Array.Empty<string>(), new FakeProbe());
        Assert.False(store.Query().Single().IsMissing);
    }

    [Fact]
    public void Search_sort_and_category_filters()
    {
        using var tmp = new TempDir();
        var store = new LibraryStore(tmp.File("l.db"));
        void Add(string name, MediaKind k, long size, int ageDays, bool fav = false) =>
            store.Upsert(new MediaItem { Path = tmp.File(name), Name = name, Kind = k, SizeBytes = size, CreatedUtc = DateTime.UtcNow.AddDays(-ageDays), IsFavorite = fav });
        Add("alpha.png", MediaKind.Screenshot, 300, 3);
        Add("Bravo.png", MediaKind.Screenshot, 100, 1, fav: true);
        Add("charlie.mp4", MediaKind.Recording, 900, 2);
        Add("50%_done.png", MediaKind.Screenshot, 10, 5);

        Assert.Equal(new[] { "Bravo.png", "charlie.mp4", "alpha.png", "50%_done.png" }, store.Query().Select(i => i.Name));
        Assert.Equal(new[] { "50%_done.png", "alpha.png", "Bravo.png", "charlie.mp4" }, store.Query(sort: LibrarySort.NameAZ).Select(i => i.Name));
        Assert.Equal("charlie.mp4", store.Query(sort: LibrarySort.SizeLargest).First().Name);
        Assert.Equal("50%_done.png", store.Query(sort: LibrarySort.SizeSmallest).First().Name);
        Assert.Equal("50%_done.png", store.Query(sort: LibrarySort.DateOldest).First().Name);
        Assert.Single(store.Query(search: "ALPH"));
        Assert.Single(store.Query(search: "50%"));        // % is literal, not a wildcard
        Assert.Equal("50%_done.png", store.Query(search: "_").Single().Name); // _ is literal, not "any char"
        Assert.Single(store.Query(LibraryCategory.Favorites));
        Assert.Single(store.Query(LibraryCategory.Recordings));
        Assert.Equal(3, store.Count(LibraryCategory.Screenshots));
    }

    [Fact]
    public void Trash_restore_and_remove_entry_never_touch_the_file()
    {
        using var tmp = new TempDir();
        var png = MakePng(tmp.Path, "keep.png");
        var store = new LibraryStore(tmp.File("l.db"));
        var item = store.Upsert(new MediaItem { Path = png, Kind = MediaKind.Screenshot, SizeBytes = 1, CreatedUtc = DateTime.UtcNow });

        store.MoveToTrash(item.Id);
        Assert.Empty(store.Query());
        Assert.Single(store.Query(LibraryCategory.Trash));
        Assert.True(File.Exists(png));
        store.Restore(item.Id);
        Assert.Single(store.Query());

        store.RemoveEntry(item.Id);
        Assert.Empty(store.Query());
        Assert.True(File.Exists(png)); // library removal must never delete the original
    }

    [Fact]
    public void Permanent_delete_uses_the_supplied_deleter_and_requires_success()
    {
        using var tmp = new TempDir();
        var png = MakePng(tmp.Path, "x.png");
        var store = new LibraryStore(tmp.File("l.db"));
        var item = store.Upsert(new MediaItem { Path = png, Kind = MediaKind.Screenshot, CreatedUtc = DateTime.UtcNow });

        Assert.Throws<IOException>(() => store.DeletePermanently(item.Id, _ => { /* deleter fails silently */ }));
        Assert.NotNull(store.Get(item.Id)); // entry kept because the file is still there

        store.DeletePermanently(item.Id, File.Delete);
        Assert.False(File.Exists(png));
        Assert.Null(store.Get(item.Id));
    }

    [Fact]
    public void Rename_moves_file_keeps_extension_and_validates()
    {
        using var tmp = new TempDir();
        var png = MakePng(tmp.Path, "old.png");
        MakePng(tmp.Path, "taken.png");
        var store = new LibraryStore(tmp.File("l.db"));
        var item = store.Upsert(new MediaItem { Path = png, Kind = MediaKind.Screenshot, CreatedUtc = DateTime.UtcNow });

        var renamed = store.Rename(item.Id, "new name");
        Assert.Equal("new name.png", renamed.Name);
        Assert.True(File.Exists(Path.Combine(tmp.Path, "new name.png")));
        Assert.False(File.Exists(png));

        Assert.Throws<IOException>(() => store.Rename(item.Id, "taken"));
        Assert.Throws<ArgumentException>(() => store.Rename(item.Id, "bad/name"));
        Assert.Throws<ArgumentException>(() => store.Rename(item.Id, "  "));
    }

    [Fact]
    public void Favorite_toggle_persists()
    {
        using var tmp = new TempDir();
        var store = new LibraryStore(tmp.File("l.db"));
        var it = store.Upsert(new MediaItem { Path = tmp.File("a.png"), Kind = MediaKind.Screenshot, CreatedUtc = DateTime.UtcNow });
        store.SetFavorite(it.Id, true);
        Assert.True(new LibraryStore(tmp.File("l.db")).Get(it.Id)!.IsFavorite);
    }

    [Fact]
    public void Thumbnails_are_created_cached_and_pruned()
    {
        using var tmp = new TempDir();
        var png = MakePng(tmp.Path, "big.png", 1200, 800);
        var svc = new ThumbnailService(tmp.File("thumbs"));
        var t1 = svc.EnsureImageThumb(png);
        Assert.NotNull(t1);
        using (var bmp = SKBitmap.Decode(t1!)) Assert.Equal(ThumbnailService.MaxEdge, Math.Max(bmp.Width, bmp.Height));
        Assert.Equal(t1, svc.EnsureImageThumb(png)); // cache hit
        Assert.Null(svc.EnsureImageThumb(tmp.File("nope.png")));
        File.WriteAllText(tmp.File("notimage.png"), "junk");
        Assert.Null(svc.EnsureImageThumb(tmp.File("notimage.png")));
        svc.Prune(Array.Empty<string?>());
        Assert.False(File.Exists(t1));
    }
}
