using PIXORA.Core;
using SkiaSharp;

namespace PIXORA.Library;

/// <summary>Generates and caches JPEG thumbnails below the app data folder.</summary>
public sealed class ThumbnailService
{
    public const int MaxEdge = 360;
    private readonly string _dir;

    public ThumbnailService(string thumbDir)
    {
        _dir = thumbDir;
        Directory.CreateDirectory(_dir);
    }

    public string ThumbPathFor(string mediaPath, long size, DateTime lastWriteUtc)
    {
        var key = $"{Path.GetFullPath(mediaPath).ToLowerInvariant()}|{size}|{lastWriteUtc.Ticks}";
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
        return Path.Combine(_dir, hash + ".jpg");
    }

    /// <summary>Returns the thumbnail path for an image, creating it when needed. Null if the image cannot be decoded.</summary>
    public string? EnsureImageThumb(string imagePath)
    {
        try
        {
            var fi = new FileInfo(imagePath);
            if (!fi.Exists) return null;
            var thumb = ThumbPathFor(imagePath, fi.Length, fi.LastWriteTimeUtc);
            if (File.Exists(thumb)) return thumb;

            using var codec = SKCodec.Create(imagePath);
            if (codec == null) return null;
            var info = codec.Info;
            float scale = Math.Min(1f, MaxEdge / (float)Math.Max(info.Width, info.Height));
            using var bmp = SKBitmap.Decode(imagePath);
            if (bmp == null) return null;
            using var resized = bmp.Resize(new SKImageInfo(Math.Max(1, (int)(info.Width * scale)), Math.Max(1, (int)(info.Height * scale))), SKSamplingOptions.Default);
            using var img = SKImage.FromBitmap(resized ?? bmp);
            using var data = img.Encode(SKEncodedImageFormat.Jpeg, 80);
            File.WriteAllBytes(thumb, data.ToArray());
            return thumb;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Thumbnail failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Removes cached thumbnails not referenced by any current library entry.</summary>
    public void Prune(IEnumerable<string?> keep)
    {
        var keepSet = new HashSet<string>(keep.Where(k => k != null)!, StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(_dir, "*.jpg"))
        {
            if (keepSet.Contains(f)) continue;
            try { File.Delete(f); } catch (IOException) { }
        }
    }
}
