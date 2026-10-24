using PIXORA.Capture;
using PIXORA.Editor.Video;
using PIXORA.Recording;
using SkiaSharp;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace PIXORA.Tests;

/// <summary>Exports real recordings through Windows.Media.Editing, so it needs the desktop like RecordingTests.</summary>
[Trait("Category", "Desktop")]
public class VideoEditingTests
{
    private static async Task<string?> RecordAsync(TempDir tmp, double seconds, bool audio)
    {
        if (ScreenCapture.GetMonitors().Count == 0 || !ScreenRecorder.IsSupported) return null;
        var m = ScreenCapture.GetMonitors().First(x => x.IsPrimary);
        var output = tmp.File("source.mp4");
        var rec = new ScreenRecorder();
        await rec.StartAsync(new RecordingOptions
        {
            Source = RecordingSource.ForRegion(new RECT(m.Bounds.Left, m.Bounds.Top, m.Bounds.Left + 640, m.Bounds.Top + 360)),
            OutputPath = output, FrameRate = 30, SystemAudio = audio,
        });
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        await rec.StopAsync();
        return output;
    }

    private static async Task<(TimeSpan duration, int video, int audio)> Inspect(string path)
    {
        var source = MediaSource.CreateFromStorageFile(await StorageFile.GetFileFromPathAsync(path));
        await source.OpenAsync();
        var item = new MediaPlaybackItem(source);
        return (source.Duration ?? TimeSpan.Zero, item.VideoTracks.Count, item.AudioTracks.Count);
    }

    [Fact]
    public async Task Cutting_out_the_middle_produces_a_shorter_playable_video_and_keeps_the_original()
    {
        using var tmp = new TempDir();
        var src = await RecordAsync(tmp, 4.5, audio: true);
        if (src == null) return;
        var before = File.ReadAllBytes(src);

        var duration = await VideoEditing.GetDurationAsync(src);
        var t = new VideoTimeline(duration);
        t.SplitAt(TimeSpan.FromSeconds(1));
        t.SplitAt(TimeSpan.FromSeconds(3));
        t.Delete(1);                                   // remove 1 s – 3 s
        t.ToggleMute(0);

        var progress = new List<double>();
        var output = tmp.File("edited.mp4");
        await VideoEditing.ExportAsync(src, t.Segments, output, new Progress<double>(progress.Add));

        var (outDur, video, _) = await Inspect(output);
        Assert.Equal(1, video);
        Assert.InRange(outDur.TotalSeconds, t.OutputDuration.TotalSeconds - 0.6, t.OutputDuration.TotalSeconds + 0.6);
        Assert.True(outDur < duration - TimeSpan.FromSeconds(1.4), $"output {outDur} vs source {duration}");
        Assert.False(File.Exists(output + ".part"));
        Assert.Equal(before, File.ReadAllBytes(src));  // original untouched
    }

    [Fact]
    public async Task Trimming_both_ends_matches_the_timeline_length()
    {
        using var tmp = new TempDir();
        var src = await RecordAsync(tmp, 3.5, audio: false);
        if (src == null) return;
        var t = new VideoTimeline(await VideoEditing.GetDurationAsync(src));
        t.SetStart(0, TimeSpan.FromSeconds(0.8));
        t.SetEnd(0, t.SourceDuration - TimeSpan.FromSeconds(0.8));
        var output = tmp.File("trimmed.mp4");
        await VideoEditing.ExportAsync(src, t.Segments, output);
        var (outDur, _, _) = await Inspect(output);
        Assert.InRange(outDur.TotalSeconds, t.OutputDuration.TotalSeconds - 0.5, t.OutputDuration.TotalSeconds + 0.5);
    }

    [Fact]
    public async Task Exporting_over_the_original_is_refused()
    {
        using var tmp = new TempDir();
        var src = await RecordAsync(tmp, 1.5, audio: false);
        if (src == null) return;
        var t = new VideoTimeline(await VideoEditing.GetDurationAsync(src));
        await Assert.ThrowsAsync<IOException>(() => VideoEditing.ExportAsync(src, t.Segments, src));
    }

    [Fact]
    public async Task Thumbnails_show_the_actual_frame_at_their_time_not_the_nearest_keyframe()
    {
        using var tmp = new TempDir();
        // red 0–2 s, blue 2–4 s, green 4–6 s; the encoder emits few keyframes for static content.
        var comp = new Windows.Media.Editing.MediaComposition();
        comp.Clips.Add(Windows.Media.Editing.MediaClip.CreateFromColor(Windows.UI.Color.FromArgb(255, 220, 30, 30), TimeSpan.FromSeconds(2)));
        comp.Clips.Add(Windows.Media.Editing.MediaClip.CreateFromColor(Windows.UI.Color.FromArgb(255, 30, 30, 220), TimeSpan.FromSeconds(2)));
        comp.Clips.Add(Windows.Media.Editing.MediaClip.CreateFromColor(Windows.UI.Color.FromArgb(255, 30, 200, 30), TimeSpan.FromSeconds(2)));
        var folder = await StorageFolder.GetFolderFromPathAsync(tmp.Path);
        var file = await folder.CreateFileAsync("colors.mp4");
        var profile = Windows.Media.MediaProperties.MediaEncodingProfile.CreateMp4(Windows.Media.MediaProperties.VideoEncodingQuality.Vga);
        profile.Audio = null;
        await comp.RenderToFileAsync(file, Windows.Media.Editing.MediaTrimmingPreference.Precise, profile);

        var thumbs = await VideoEditing.GetThumbnailsAsync(file.Path, 3, 48);
        SKColor Mid(VideoThumbnail t) { using var b = SKBitmap.Decode(t.Jpeg); return b.GetPixel(b.Width / 2, b.Height / 2); }
        var first = Mid(thumbs[0]); var middle = Mid(thumbs[1]); var last = Mid(thumbs[2]);
        Assert.True(first.Red > 150 && first.Blue < 100, $"first {first}");
        Assert.True(middle.Blue > 150 && middle.Red < 100, $"middle {middle}");
        Assert.True(last.Green > 150 && last.Red < 100, $"last {last}");
    }

    [Fact]
    public async Task Thumbnails_are_decodable_images_spread_across_the_video()
    {
        using var tmp = new TempDir();
        var src = await RecordAsync(tmp, 2.5, audio: false);
        if (src == null) return;
        var thumbs = await VideoEditing.GetThumbnailsAsync(src, 6, 60);
        Assert.Equal(6, thumbs.Count);
        Assert.True(thumbs.Select(x => x.Time).SequenceEqual(thumbs.Select(x => x.Time).OrderBy(x => x)));
        using var bmp = SKBitmap.Decode(thumbs[0].Jpeg);
        Assert.NotNull(bmp);
        Assert.Equal(60, bmp.Height);
    }
}
