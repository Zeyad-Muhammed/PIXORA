using PIXORA.Editor.Video;

namespace PIXORA.Tests;

public class VideoTimelineTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void New_timeline_keeps_the_whole_video_and_is_not_edited()
    {
        var t = new VideoTimeline(S(10));
        Assert.Single(t.Segments);
        Assert.Equal(S(10), t.OutputDuration);
        Assert.False(t.IsEdited);
        Assert.Throws<ArgumentOutOfRangeException>(() => new VideoTimeline(TimeSpan.Zero));
    }

    [Fact]
    public void Split_then_delete_removes_that_part_from_the_output()
    {
        var t = new VideoTimeline(S(10));
        Assert.True(t.SplitAt(S(3)));
        Assert.True(t.SplitAt(S(6)));
        Assert.Equal(3, t.Segments.Count);
        Assert.True(t.Delete(1));
        Assert.Equal(new[] { (S(0), S(3)), (S(6), S(10)) }, t.Segments.Select(s => (s.Start, s.End)));
        Assert.Equal(S(7), t.OutputDuration);
        Assert.Equal(S(3), t.RemovedDuration);
        Assert.True(t.IsEdited);
    }

    [Fact]
    public void Split_refuses_points_too_close_to_an_edge_or_in_a_gap()
    {
        var t = new VideoTimeline(S(10));
        Assert.False(t.SplitAt(TimeSpan.FromMilliseconds(50)));
        Assert.True(t.SplitAt(S(5)));
        Assert.True(t.Delete(1));          // keep 0–5
        Assert.False(t.SplitAt(S(7)));     // removed part
    }

    [Fact]
    public void The_last_segment_cannot_be_deleted()
    {
        var t = new VideoTimeline(S(4));
        Assert.False(t.Delete(0));
        Assert.Single(t.Segments);
    }

    [Fact]
    public void Trims_are_clamped_to_neighbours_bounds_and_minimum_length()
    {
        var t = new VideoTimeline(S(10));
        t.SplitAt(S(5));
        t.SetEnd(0, S(8));                 // cannot overlap the next segment
        Assert.Equal(S(5), t.Segments[0].End);
        t.SetStart(1, S(2));               // cannot overlap the previous segment
        Assert.Equal(S(5), t.Segments[1].Start);
        t.SetStart(0, S(-3));
        Assert.Equal(S(0), t.Segments[0].Start);
        t.SetEnd(1, S(99));
        Assert.Equal(S(10), t.Segments[1].End);
        t.SetStart(0, S(4.99));            // keeps at least MinSegment
        Assert.Equal(S(5) - VideoTimeline.MinSegment, t.Segments[0].Start);
        t.SetEnd(0, S(2));
        Assert.Equal(t.Segments[0].Start + VideoTimeline.MinSegment, t.Segments[0].End);
    }

    [Fact]
    public void Trimming_the_ends_shortens_the_output()
    {
        var t = new VideoTimeline(S(10));
        t.SetStart(0, S(1.5));
        t.SetEnd(0, S(9));
        Assert.Equal(S(7.5), t.OutputDuration);
    }

    [Fact]
    public void Undo_and_redo_cover_every_edit_and_a_drag_is_one_step()
    {
        var t = new VideoTimeline(S(10));
        t.SplitAt(S(4));
        t.ToggleMute(1);
        // A drag: one checkpoint, many unrecorded moves.
        t.Checkpoint();
        t.SetEnd(1, S(9.5), record: false);
        t.SetEnd(1, S(9), record: false);
        Assert.Equal(S(9), t.Segments[1].End);
        t.Undo();
        Assert.Equal(S(10), t.Segments[1].End);
        t.Undo();
        Assert.False(t.Segments[1].Muted);
        t.Undo();
        Assert.Single(t.Segments);
        Assert.False(t.CanUndo);
        t.Redo(); t.Redo();
        Assert.True(t.Segments[1].Muted);
        t.Delete(0);                        // new edit clears redo
        Assert.False(t.CanRedo);
    }

    [Fact]
    public void Mute_all_and_reset()
    {
        var t = new VideoTimeline(S(6));
        t.SplitAt(S(2));
        t.SetAllMuted(true);
        Assert.All(t.Segments, s => Assert.True(s.Muted));
        t.Reset();
        Assert.False(t.IsEdited);
        t.Undo();
        Assert.Equal(2, t.Segments.Count);
    }

    [Fact]
    public void Playback_positions_skip_removed_parts_and_map_to_output_time()
    {
        var t = new VideoTimeline(S(10));
        t.SplitAt(S(2)); t.SplitAt(S(5));
        t.Delete(1);                        // keep 0–2 and 5–10
        Assert.Equal(S(1), t.PlayablePosition(S(1)));
        Assert.Equal(S(5), t.PlayablePosition(S(3)));
        t.SetEnd(1, S(8));
        Assert.Null(t.PlayablePosition(S(9)));
        Assert.Equal(S(2), t.ToOutputTime(S(4)));
        Assert.Equal(S(3), t.ToOutputTime(S(6)));
        Assert.Equal(-1, t.IndexAt(S(3)));
        Assert.Equal(1, t.IndexAt(S(6)));
    }

    [Fact]
    public void Changed_fires_on_edits()
    {
        var t = new VideoTimeline(S(5));
        int n = 0;
        t.Changed += () => n++;
        t.SplitAt(S(2)); t.ToggleMute(0); t.Undo();
        Assert.Equal(3, n);
    }
}
