using Microsoft.UI.Dispatching;
using PIXORA.App.Interop;
using PIXORA.Core;
using PIXORA.Library;
using PIXORA.Recording;

namespace PIXORA.App.Services;

/// <summary>UI-facing wrapper over the SQLite library: runs I/O off the UI thread and raises <see cref="Changed"/> on it.</summary>
public sealed class MediaLibraryService
{
    private readonly LibraryStore _store;
    private readonly ThumbnailService _thumbs;
    private readonly SettingsStore _settings;
    private readonly MediaProbe _probe = new();
    private readonly DispatcherQueue _ui;
    private readonly SemaphoreSlim _scanGate = new(1, 1);

    public MediaLibraryService(LibraryStore store, ThumbnailService thumbs, SettingsStore settings, DispatcherQueue ui)
    {
        _store = store; _thumbs = thumbs; _settings = settings; _ui = ui;
    }

    public event Action? Changed;

    private void RaiseChanged() => _ui.TryEnqueue(() => Changed?.Invoke());

    public Task<IReadOnlyList<MediaItem>> QueryAsync(LibraryCategory cat, string? search, LibrarySort sort) =>
        Task.Run(() => _store.Query(cat, search, sort));

    public Task<int> CountAsync(LibraryCategory cat) => Task.Run(() => _store.Count(cat));

    /// <summary>Re-indexes the configured folders, flags missing files and fills in thumbnails.</summary>
    public async Task<ScanSummary> RefreshAsync()
    {
        await _scanGate.WaitAsync();
        try
        {
            var s = _settings.Current;
            var summary = await Task.Run(() => _store.ScanAsync(new[] { s.ScreenshotDir }, new[] { s.RecordingDir }, _probe));
            await FillThumbnailsAsync();
            RaiseChanged();
            return summary;
        }
        finally { _scanGate.Release(); }
    }

    private async Task FillThumbnailsAsync()
    {
        var items = await Task.Run(() => _store.Query(LibraryCategory.All).Concat(_store.Query(LibraryCategory.Trash)).ToList());
        foreach (var it in items)
        {
            if (it.IsMissing) continue;
            if (it.ThumbPath != null && File.Exists(it.ThumbPath)) continue;
            var thumb = await CreateThumbAsync(it.Path, it.Kind);
            if (thumb != null) await Task.Run(() => _store.SetThumb(it.Id, thumb));
        }
    }

    private async Task<string?> CreateThumbAsync(string path, MediaKind kind)
    {
        if (kind == MediaKind.Screenshot) return await Task.Run(() => _thumbs.EnsureImageThumb(path));
        var fi = new FileInfo(path);
        if (!fi.Exists) return null;
        var target = _thumbs.ThumbPathFor(path, fi.Length, fi.LastWriteTimeUtc);
        if (File.Exists(target)) return target;
        return await MediaProbe.CreateVideoThumbnailAsync(path, target) ? target : null;
    }

    /// <summary>Indexes a newly created capture or recording.</summary>
    public async Task<MediaItem> AddAsync(string path, MediaKind kind)
    {
        MediaProbeResult? info = null;
        try { info = await _probe.ProbeAsync(path, kind); } catch (Exception ex) { Log.Warn($"Probe failed: {ex.Message}"); }
        var fi = new FileInfo(path);
        var item = await Task.Run(() => _store.Upsert(new MediaItem
        {
            Path = path, Name = fi.Name, Kind = kind, SizeBytes = fi.Exists ? fi.Length : 0, CreatedUtc = DateTime.UtcNow,
            Width = info?.Width ?? 0, Height = info?.Height ?? 0, DurationSeconds = info?.DurationSeconds ?? 0,
        }));
        var thumb = await CreateThumbAsync(path, kind);
        if (thumb != null) await Task.Run(() => _store.SetThumb(item.Id, thumb));
        RaiseChanged();
        return item with { ThumbPath = thumb };
    }

    public async Task<MediaItem> RenameAsync(long id, string newName)
    {
        var r = await Task.Run(() => _store.Rename(id, newName));
        RaiseChanged();
        return r;
    }

    public async Task SetFavoriteAsync(IEnumerable<long> ids, bool fav)
    {
        await Task.Run(() => { foreach (var id in ids) _store.SetFavorite(id, fav); });
        RaiseChanged();
    }

    public async Task TrashAsync(IEnumerable<long> ids)
    {
        await Task.Run(() => { foreach (var id in ids) _store.MoveToTrash(id); });
        RaiseChanged();
    }

    public async Task RestoreAsync(IEnumerable<long> ids)
    {
        await Task.Run(() => { foreach (var id in ids) _store.Restore(id); });
        RaiseChanged();
    }

    /// <summary>Removes entries from the library while leaving files on disk.</summary>
    public async Task RemoveEntriesAsync(IEnumerable<long> ids)
    {
        await Task.Run(() => { foreach (var id in ids) _store.RemoveEntry(id); });
        RaiseChanged();
    }

    /// <summary>Sends files to the Recycle Bin and removes their entries. Returns names that could not be deleted.</summary>
    public async Task<IReadOnlyList<string>> DeletePermanentlyAsync(IEnumerable<long> ids)
    {
        var failed = new List<string>();
        await Task.Run(() =>
        {
            foreach (var id in ids)
            {
                try { _store.DeletePermanently(id, p => Win32.RecycleFile(p)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed.Add(_store.Get(id)?.Name ?? id.ToString());
                    Log.Warn($"Delete failed: {ex.Message}");
                }
            }
        });
        RaiseChanged();
        return failed;
    }

    public MediaItem? Get(long id) => _store.Get(id);
    public MediaItem? GetByPath(string path) => _store.GetByPath(path);
}
