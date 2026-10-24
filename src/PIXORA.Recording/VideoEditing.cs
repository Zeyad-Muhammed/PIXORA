using System.Runtime.InteropServices.WindowsRuntime;
using PIXORA.Core;
using PIXORA.Editor.Video;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace PIXORA.Recording;

public sealed record VideoThumbnail(TimeSpan Time, byte[] Jpeg);

/// <summary>
/// Renders a <see cref="VideoTimeline"/> cut list with the Windows media composition engine
/// (Windows.Media.Editing) and extracts timeline thumbnails. The source file is only read.
/// </summary>
public static class VideoEditing
{
    public static async Task<TimeSpan> GetDurationAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        var clip = await MediaClip.CreateFromFileAsync(file);
        return clip.OriginalDuration;
    }

    /// <summary>Evenly spaced frames across the source, scaled to <paramref name="height"/> pixels.</summary>
    public static async Task<IReadOnlyList<VideoThumbnail>> GetThumbnailsAsync(string path, int count, int height = 72, CancellationToken ct = default)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        var clip = await MediaClip.CreateFromFileAsync(file);
        var comp = new MediaComposition();
        comp.Clips.Add(clip);
        var props = clip.GetVideoEncodingProperties();
        int w = props != null && props.Height > 0 ? (int)Math.Round(height * (double)props.Width / props.Height) : height * 16 / 9;
        var dur = clip.OriginalDuration;
        count = Math.Max(1, count);
        var times = Enumerable.Range(0, count).Select(i => TimeSpan.FromTicks(dur.Ticks * (2 * i + 1) / (2 * count))).ToList();
        // One call per frame: the batch GetThumbnailsAsync returned the first frame for every time on Windows 11 26200.
        var result = new List<VideoThumbnail>();
        foreach (var t in times)
        {
            ct.ThrowIfCancellationRequested();
            using var s = await comp.GetThumbnailAsync(t, w, height, VideoFramePrecision.NearestFrame).AsTask(ct);
            var bytes = new byte[s.Size];
            await s.ReadAsync(bytes.AsBuffer(), (uint)s.Size, Windows.Storage.Streams.InputStreamOptions.None).AsTask(ct);
            result.Add(new VideoThumbnail(t, bytes));
        }
        return result;
    }

    /// <summary>
    /// Writes the kept segments of <paramref name="sourcePath"/> to <paramref name="outputPath"/> (MP4, same encoding
    /// settings as the source). Muted segments keep their video with silent audio. Writes to a temp file first.
    /// </summary>
    public static async Task ExportAsync(string sourcePath, IReadOnlyList<VideoSegment> segments, string outputPath,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (segments.Count == 0) throw new ArgumentException("Nothing to export: every part was removed.");
        var src = Path.GetFullPath(sourcePath);
        var dst = Path.GetFullPath(outputPath);
        if (string.Equals(src, dst, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose a different file name: the original recording is read while exporting and cannot be overwritten.");
        var dir = Path.GetDirectoryName(dst)!;
        var problem = SettingsValidator.ProbeWritable(dir);
        if (problem != null) throw new IOException(problem);

        var file = await StorageFile.GetFileFromPathAsync(src);
        var comp = new MediaComposition();
        foreach (var seg in segments)
        {
            var clip = await MediaClip.CreateFromFileAsync(file);
            var full = clip.OriginalDuration;
            clip.TrimTimeFromStart = seg.Start < TimeSpan.Zero ? TimeSpan.Zero : seg.Start;
            clip.TrimTimeFromEnd = seg.End >= full ? TimeSpan.Zero : full - seg.End;
            if (seg.Muted) clip.Volume = 0;
            comp.Clips.Add(clip);
        }

        var profile = await MediaEncodingProfile.CreateFromFileAsync(file);
        var part = dst + ".part";
        var folder = await StorageFolder.GetFolderFromPathAsync(dir);
        var outFile = await folder.CreateFileAsync(Path.GetFileName(part), CreationCollisionOption.ReplaceExisting);
        try
        {
            var op = comp.RenderToFileAsync(outFile, MediaTrimmingPreference.Precise, profile);
            op.Progress = (_, pct) => progress?.Report(Math.Clamp(pct / 100.0, 0, 1));
            using (ct.Register(() => op.Cancel()))
            {
                var result = await op;
                ct.ThrowIfCancellationRequested();
                if (result != TranscodeFailureReason.None)
                    throw new IOException($"Windows could not encode the video ({result}).");
            }
            File.Move(part, dst, true);
            progress?.Report(1);
            Log.Info($"Video exported: {segments.Count} segment(s) -> {Path.GetFileName(dst)}");
        }
        catch
        {
            try { if (File.Exists(part)) File.Delete(part); } catch (IOException) { }
            throw;
        }
    }
}
