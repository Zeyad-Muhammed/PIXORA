using Microsoft.Data.Sqlite;
using PIXORA.Core;

namespace PIXORA.Library;

public sealed record MediaProbeResult(int Width, int Height, double DurationSeconds);

/// <summary>Reads dimensions/duration of a media file. Video probing is supplied by the Recording layer.</summary>
public interface IMediaProbe
{
    Task<MediaProbeResult?> ProbeAsync(string path, MediaKind kind, CancellationToken ct = default);
}

public sealed record ScanSummary(int Added, int Updated, int MarkedMissing, int Restored);

/// <summary>Persistent SQLite index of screenshots and recordings. The files themselves live wherever the user keeps them.</summary>
public sealed class LibraryStore
{
    public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".webp", ".bmp" };
    public static readonly string[] VideoExtensions = { ".mp4", ".mkv", ".wmv" };

    private readonly string _connectionString;

    public LibraryStore(string dbPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString();
        Initialize();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private void Initialize()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS media (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                path TEXT NOT NULL UNIQUE COLLATE NOCASE,
                name TEXT NOT NULL,
                kind INTEGER NOT NULL,
                size INTEGER NOT NULL DEFAULT 0,
                created_utc INTEGER NOT NULL,
                width INTEGER NOT NULL DEFAULT 0,
                height INTEGER NOT NULL DEFAULT 0,
                duration REAL NOT NULL DEFAULT 0,
                favorite INTEGER NOT NULL DEFAULT 0,
                trashed INTEGER NOT NULL DEFAULT 0,
                missing INTEGER NOT NULL DEFAULT 0,
                thumb TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_media_created ON media(created_utc);
            """;
        cmd.ExecuteNonQuery();
    }

    private const string Columns = "id,path,name,kind,size,created_utc,width,height,duration,favorite,trashed,missing,thumb";

    private static MediaItem Read(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Path = r.GetString(1),
        Name = r.GetString(2),
        Kind = (MediaKind)r.GetInt32(3),
        SizeBytes = r.GetInt64(4),
        CreatedUtc = DateTime.SpecifyKind(DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(5)).UtcDateTime, DateTimeKind.Utc),
        Width = r.GetInt32(6),
        Height = r.GetInt32(7),
        DurationSeconds = r.GetDouble(8),
        IsFavorite = r.GetInt32(9) != 0,
        IsTrashed = r.GetInt32(10) != 0,
        IsMissing = r.GetInt32(11) != 0,
        ThumbPath = r.IsDBNull(12) ? null : r.GetString(12),
    };

    public MediaItem? Get(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM media WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    public MediaItem? GetByPath(string path)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM media WHERE path=$p";
        cmd.Parameters.AddWithValue("$p", Path.GetFullPath(path));
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    /// <summary>Inserts or updates by path and returns the stored item.</summary>
    public MediaItem Upsert(MediaItem item)
    {
        var full = Path.GetFullPath(item.Path);
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO media (path,name,kind,size,created_utc,width,height,duration,favorite,trashed,missing,thumb)
            VALUES ($p,$n,$k,$s,$c,$w,$h,$d,$f,$t,$m,$th)
            ON CONFLICT(path) DO UPDATE SET
              name=excluded.name, kind=excluded.kind, size=excluded.size, width=excluded.width,
              height=excluded.height, duration=excluded.duration, missing=excluded.missing,
              thumb=COALESCE(excluded.thumb, media.thumb);
            """;
        cmd.Parameters.AddWithValue("$p", full);
        cmd.Parameters.AddWithValue("$n", string.IsNullOrEmpty(item.Name) ? System.IO.Path.GetFileName(full) : item.Name);
        cmd.Parameters.AddWithValue("$k", (int)item.Kind);
        cmd.Parameters.AddWithValue("$s", item.SizeBytes);
        cmd.Parameters.AddWithValue("$c", new DateTimeOffset(item.CreatedUtc, TimeSpan.Zero).ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$w", item.Width);
        cmd.Parameters.AddWithValue("$h", item.Height);
        cmd.Parameters.AddWithValue("$d", item.DurationSeconds);
        cmd.Parameters.AddWithValue("$f", item.IsFavorite ? 1 : 0);
        cmd.Parameters.AddWithValue("$t", item.IsTrashed ? 1 : 0);
        cmd.Parameters.AddWithValue("$m", item.IsMissing ? 1 : 0);
        cmd.Parameters.AddWithValue("$th", (object?)item.ThumbPath ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        return GetByPath(full)!;
    }

    public IReadOnlyList<MediaItem> Query(LibraryCategory category = LibraryCategory.All, string? search = null,
        LibrarySort sort = LibrarySort.DateNewest, MediaKind? kindFilter = null)
    {
        var where = new List<string>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        switch (category)
        {
            case LibraryCategory.Trash: where.Add("trashed=1"); break;
            case LibraryCategory.Screenshots: where.Add("trashed=0 AND kind=0"); break;
            case LibraryCategory.Recordings: where.Add("trashed=0 AND kind=1"); break;
            case LibraryCategory.Favorites: where.Add("trashed=0 AND favorite=1"); break;
            default: where.Add("trashed=0"); break;
        }
        if (kindFilter is { } k) { where.Add("kind=$kind"); cmd.Parameters.AddWithValue("$kind", (int)k); }
        if (!string.IsNullOrWhiteSpace(search))
        {
            where.Add("name LIKE $q ESCAPE '\\'");
            var escaped = search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            cmd.Parameters.AddWithValue("$q", $"%{escaped}%");
        }
        var order = sort switch
        {
            LibrarySort.DateOldest => "created_utc ASC",
            LibrarySort.NameAZ => "name COLLATE NOCASE ASC",
            LibrarySort.NameZA => "name COLLATE NOCASE DESC",
            LibrarySort.SizeLargest => "size DESC",
            LibrarySort.SizeSmallest => "size ASC",
            _ => "created_utc DESC",
        };
        cmd.CommandText = $"SELECT {Columns} FROM media WHERE {string.Join(" AND ", where)} ORDER BY {order}, id DESC";
        var list = new List<MediaItem>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    public int Count(LibraryCategory category)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = category switch
        {
            LibraryCategory.Trash => "SELECT COUNT(*) FROM media WHERE trashed=1",
            LibraryCategory.Screenshots => "SELECT COUNT(*) FROM media WHERE trashed=0 AND kind=0",
            LibraryCategory.Recordings => "SELECT COUNT(*) FROM media WHERE trashed=0 AND kind=1",
            LibraryCategory.Favorites => "SELECT COUNT(*) FROM media WHERE trashed=0 AND favorite=1",
            _ => "SELECT COUNT(*) FROM media WHERE trashed=0",
        };
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void SetFavorite(long id, bool favorite) => Exec("UPDATE media SET favorite=$v WHERE id=$id", ("$v", favorite ? 1 : 0), ("$id", id));
    public void SetThumb(long id, string? thumbPath) => Exec("UPDATE media SET thumb=$v WHERE id=$id", ("$v", (object?)thumbPath ?? DBNull.Value), ("$id", id));
    public void MoveToTrash(long id) => Exec("UPDATE media SET trashed=1 WHERE id=$id", ("$id", id));
    public void Restore(long id) => Exec("UPDATE media SET trashed=0 WHERE id=$id", ("$id", id));

    /// <summary>Removes the library entry only. The file on disk is not touched.</summary>
    public void RemoveEntry(long id) => Exec("DELETE FROM media WHERE id=$id", ("$id", id));

    /// <summary>Permanently deletes the file (through <paramref name="deleteFile"/>, e.g. the Recycle Bin) then removes the entry.</summary>
    public void DeletePermanently(long id, Action<string> deleteFile)
    {
        var item = Get(id);
        if (item == null) return;
        if (File.Exists(item.Path)) deleteFile(item.Path);
        if (File.Exists(item.Path)) throw new IOException($"\"{item.Name}\" could not be deleted.");
        RemoveEntry(id);
    }

    /// <summary>Renames the file on disk (keeping its extension) and the index entry. Returns the new item.</summary>
    public MediaItem Rename(long id, string newBaseName)
    {
        var item = Get(id) ?? throw new KeyNotFoundException("Library entry not found.");
        if (string.IsNullOrWhiteSpace(newBaseName)) throw new ArgumentException("Name cannot be empty.");
        if (newBaseName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Name contains characters that are not allowed in file names.");
        var ext = System.IO.Path.GetExtension(item.Path);
        var newPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(item.Path)!, newBaseName.Trim() + ext);
        if (!string.Equals(newPath, item.Path, StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(newPath)) throw new IOException($"A file named \"{System.IO.Path.GetFileName(newPath)}\" already exists.");
            if (!File.Exists(item.Path)) throw new FileNotFoundException("The file is missing and cannot be renamed.", item.Path);
            File.Move(item.Path, newPath);
        }
        Exec("UPDATE media SET path=$p, name=$n WHERE id=$id", ("$p", newPath), ("$n", System.IO.Path.GetFileName(newPath)), ("$id", id));
        return Get(id)!;
    }

    /// <summary>
    /// Synchronises the index with the watched folders: adds new files, refreshes changed ones and
    /// flags entries whose file is gone as missing (they are never deleted automatically).
    /// </summary>
    public async Task<ScanSummary> ScanAsync(IEnumerable<string> screenshotDirs, IEnumerable<string> recordingDirs,
        IMediaProbe? probe = null, CancellationToken ct = default)
    {
        int added = 0, updated = 0, missing = 0, restored = 0;

        async Task Index(string dir, MediaKind kind, string[] exts)
        {
            if (!Directory.Exists(dir)) return;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn($"Cannot scan {dir}: {ex.Message}"); return; }

            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                if (!exts.Contains(System.IO.Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)) continue;
                FileInfo fi;
                try { fi = new FileInfo(f); if (!fi.Exists || fi.Length == 0) continue; }
                catch (IOException) { continue; }

                var existing = GetByPath(f);
                if (existing != null && !existing.IsMissing && existing.SizeBytes == fi.Length && existing.Width > 0) continue;

                MediaProbeResult? info = null;
                if (probe != null)
                {
                    try { info = await probe.ProbeAsync(f, kind, ct).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"Probe failed for {fi.Name}: {ex.Message}"); }
                }
                Upsert(new MediaItem
                {
                    Path = f, Name = fi.Name, Kind = kind, SizeBytes = fi.Length,
                    CreatedUtc = fi.CreationTimeUtc,
                    Width = info?.Width ?? existing?.Width ?? 0,
                    Height = info?.Height ?? existing?.Height ?? 0,
                    DurationSeconds = info?.DurationSeconds ?? existing?.DurationSeconds ?? 0,
                    IsFavorite = existing?.IsFavorite ?? false,
                    IsTrashed = existing?.IsTrashed ?? false,
                });
                if (existing == null) added++; else { updated++; if (existing.IsMissing) restored++; }
            }
        }

        foreach (var d in screenshotDirs.Distinct(StringComparer.OrdinalIgnoreCase)) await Index(d, MediaKind.Screenshot, ImageExtensions).ConfigureAwait(false);
        foreach (var d in recordingDirs.Distinct(StringComparer.OrdinalIgnoreCase)) await Index(d, MediaKind.Recording, VideoExtensions).ConfigureAwait(false);

        // Detect moved/deleted files.
        foreach (var item in AllRaw())
        {
            bool exists = File.Exists(item.Path);
            if (!exists && !item.IsMissing) { Exec("UPDATE media SET missing=1 WHERE id=$id", ("$id", item.Id)); missing++; }
            else if (exists && item.IsMissing) { Exec("UPDATE media SET missing=0 WHERE id=$id", ("$id", item.Id)); restored++; }
        }
        return new ScanSummary(added, updated, missing, restored);
    }

    private List<MediaItem> AllRaw()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM media";
        var list = new List<MediaItem>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    private void Exec(string sql, params (string Name, object? Value)[] args)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}
