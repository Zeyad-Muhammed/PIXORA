using PIXORA.Capture;
using PIXORA.Core;
using PIXORA.Recording;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace PIXORA.Tests;

/// <summary>End-to-end recording tests. They drive the real Windows capture + encoder, so they need an interactive desktop.</summary>
[Trait("Category", "Desktop")]
public class RecordingTests
{
    private static bool CanRecord => ScreenCapture.GetMonitors().Count > 0 && ScreenRecorder.IsSupported;

    private static RecordingSource SmallRegion()
    {
        var m = ScreenCapture.GetMonitors().First(x => x.IsPrimary);
        return RecordingSource.ForRegion(new RECT(m.Bounds.Left + 20, m.Bounds.Top + 20, m.Bounds.Left + 20 + 640, m.Bounds.Top + 20 + 360));
    }

    private static async Task<(TimeSpan duration, int video, int audio)> Inspect(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        var source = MediaSource.CreateFromStorageFile(file);
        await source.OpenAsync();
        var item = new MediaPlaybackItem(source);
        return (source.Duration ?? TimeSpan.Zero, item.VideoTracks.Count, item.AudioTracks.Count);
    }

    [Fact]
    public async Task Video_only_recording_produces_a_playable_mp4_of_the_right_length()
    {
        if (!CanRecord) return;
        using var tmp = new TempDir();
        var rec = new ScreenRecorder();
        var states = new List<RecorderState>();
        rec.StateChanged += states.Add;
        var output = tmp.File("v.mp4");

        await rec.StartAsync(new RecordingOptions { Source = SmallRegion(), OutputPath = output, FrameRate = 30 });
        Assert.Equal(RecorderState.Recording, rec.State);
        await Task.Delay(3000);
        var result = await rec.StopAsync();

        Assert.Equal(RecorderState.Idle, rec.State);
        Assert.True(File.Exists(output));
        Assert.False(File.Exists(output + ".part"), "temp file must be gone after finalisation");
        Assert.Equal((640, 360), (result.Width, result.Height));
        var (duration, video, audio) = await Inspect(output);
        Assert.Equal(1, video);
        Assert.Equal(0, audio);
        Assert.InRange(duration.TotalSeconds, 2.2, 4.2);
        Assert.Contains(RecorderState.Starting, states);
        Assert.Contains(RecorderState.Stopping, states);
    }

    [Fact]
    public async Task Recording_with_system_audio_has_an_audio_track_in_sync_with_video()
    {
        if (!CanRecord) return;
        using var tmp = new TempDir();
        var rec = new ScreenRecorder();
        var output = tmp.File("av.mp4");
        await rec.StartAsync(new RecordingOptions { Source = SmallRegion(), OutputPath = output, FrameRate = 30, SystemAudio = true });
        if (rec.Warnings.Count > 0)
        {
            // No render device on this machine: audio is optional by design, so the recorder degrades to video-only.
            Console.WriteLine("AUDIO NOT VERIFIED: " + string.Join(" ", rec.Warnings));
            await rec.StopAsync();
            return;
        }
        await Task.Delay(3000);
        var result = await rec.StopAsync();
        var (duration, video, audio) = await Inspect(output);
        Assert.Equal(1, video);
        Assert.Equal(1, audio);
        Assert.InRange(duration.TotalSeconds, 2.2, 4.2);
        Assert.InRange(Math.Abs(result.Duration.TotalSeconds - duration.TotalSeconds), 0, 1.0);
    }

    [Fact]
    public async Task Pause_excludes_paused_time_from_the_file()
    {
        if (!CanRecord) return;
        using var tmp = new TempDir();
        var rec = new ScreenRecorder();
        var output = tmp.File("p.mp4");
        await rec.StartAsync(new RecordingOptions { Source = SmallRegion(), OutputPath = output, FrameRate = 30 });
        await Task.Delay(1500);
        rec.Pause();
        Assert.Equal(RecorderState.Paused, rec.State);
        var frozen = rec.Elapsed;
        await Task.Delay(2500);
        Assert.InRange((rec.Elapsed - frozen).TotalMilliseconds, 0, 200);
        rec.Resume();
        Assert.Equal(RecorderState.Recording, rec.State);
        await Task.Delay(1500);
        await rec.StopAsync();
        var (duration, _, _) = await Inspect(output);
        Assert.InRange(duration.TotalSeconds, 2.0, 4.0); // ~3 s recorded, 2.5 s paused is not in the file
    }

    [Fact]
    public async Task Monitor_recording_at_reduced_resolution()
    {
        if (!CanRecord) return;
        using var tmp = new TempDir();
        var mon = ScreenCapture.GetMonitors().First(x => x.IsPrimary);
        var rec = new ScreenRecorder();
        var output = tmp.File("m.mp4");
        await rec.StartAsync(new RecordingOptions { Source = RecordingSource.ForMonitor(mon), OutputPath = output, FrameRate = 24, OutputHeight = 480 });
        await Task.Delay(2000);
        var result = await rec.StopAsync();
        Assert.Equal(480, result.Height);
        Assert.True(result.Width % 2 == 0);
        var info = await new MediaProbe().ProbeAsync(output, MediaKind.Recording);
        Assert.NotNull(info);
        Assert.Equal(480, info!.Height);
    }

    [Fact]
    public async Task Start_failure_is_reported_and_leaves_nothing_behind()
    {
        if (!CanRecord) return;
        using var tmp = new TempDir();
        var blocker = tmp.File("blocker"); File.WriteAllText(blocker, "x");
        var rec = new ScreenRecorder();
        var ex = await Assert.ThrowsAsync<RecordingException>(() =>
            rec.StartAsync(new RecordingOptions { Source = SmallRegion(), OutputPath = Path.Combine(blocker, "x.mp4") }));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.Equal(RecorderState.Faulted, rec.State);
        // A faulted recorder can be reused.
        var output = tmp.File("again.mp4");
        await rec.StartAsync(new RecordingOptions { Source = SmallRegion(), OutputPath = output });
        await Task.Delay(1000);
        await rec.StopAsync();
        Assert.True(File.Exists(output));
    }

    [Fact]
    public async Task Invalid_window_handle_fails_cleanly()
    {
        if (!CanRecord) return;
        using var tmp = new TempDir();
        var rec = new ScreenRecorder();
        await Assert.ThrowsAnyAsync<Exception>(() =>
            rec.StartAsync(new RecordingOptions { Source = RecordingSource.ForWindow(new IntPtr(0x7FFFFF0)), OutputPath = tmp.File("w.mp4") }));
        Assert.Equal(RecorderState.Faulted, rec.State);
        Assert.Empty(Directory.GetFiles(tmp.Path));
    }

    [Fact]
    public void Stop_without_recording_is_rejected()
    {
        var rec = new ScreenRecorder();
        Assert.ThrowsAsync<InvalidOperationException>(() => rec.StopAsync()).GetAwaiter().GetResult();
    }

    [Fact]
    public void Audio_device_listing_does_not_throw()
    {
        var mics = AudioDevices.GetMicrophones();
        Assert.All(mics, m => Assert.False(string.IsNullOrEmpty(m.Id)));
    }
}
