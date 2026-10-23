using System.Runtime.InteropServices.WindowsRuntime;
using PIXORA.Core;
using PIXORA.Library;
using SkiaSharp;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace PIXORA.Recording;

/// <summary>Reads image and video metadata so the library can show size, duration and resolution.</summary>
public sealed class MediaProbe : IMediaProbe
{
    public async Task<MediaProbeResult?> ProbeAsync(string path, MediaKind kind, CancellationToken ct = default)
    {
        if (kind == MediaKind.Screenshot)
        {
            using var codec = SKCodec.Create(path);
            return codec == null ? null : new MediaProbeResult(codec.Info.Width, codec.Info.Height, 0);
        }
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)).AsTask(ct).ConfigureAwait(false);
        var props = await file.Properties.GetVideoPropertiesAsync().AsTask(ct).ConfigureAwait(false);
        if (props.Duration == TimeSpan.Zero && props.Width == 0) return null;
        return new MediaProbeResult((int)props.Width, (int)props.Height, props.Duration.TotalSeconds);
    }

    /// <summary>Extracts a poster frame for a video into <paramref name="thumbPath"/>. Returns false if Windows has no thumbnail.</summary>
    public static async Task<bool> CreateVideoThumbnailAsync(string videoPath, string thumbPath, uint size = 360)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(videoPath));
            using var thumb = await file.GetThumbnailAsync(ThumbnailMode.VideosView, size, ThumbnailOptions.UseCurrentScale);
            if (thumb == null || thumb.Size == 0) return false;
            var bytes = new byte[thumb.Size];
            using var reader = new Windows.Storage.Streams.DataReader(thumb);
            await reader.LoadAsync((uint)thumb.Size);
            reader.ReadBytes(bytes);
            using var bmp = SKBitmap.Decode(bytes);
            if (bmp == null) return false;
            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Jpeg, 80);
            Directory.CreateDirectory(Path.GetDirectoryName(thumbPath)!);
            await File.WriteAllBytesAsync(thumbPath, data.ToArray());
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Video thumbnail failed: {ex.Message}");
            return false;
        }
    }
}
