namespace PIXORA.Editor.Video;

/// <summary>A kept part of the source video, in source time.</summary>
public sealed record VideoSegment(TimeSpan Start, TimeSpan End, bool Muted = false)
{
    public TimeSpan Duration => End - Start;
    public bool Contains(TimeSpan t) => t >= Start && t < End;
}

/// <summary>
/// Non-destructive cut list over one source video: an ordered set of non-overlapping kept segments.
/// Gaps between segments are removed from the output. Every edit is undoable; the source file is never modified.
/// </summary>
public sealed class VideoTimeline
{
    public static readonly TimeSpan MinSegment = TimeSpan.FromMilliseconds(200);

    private List<VideoSegment> _segments;
    private readonly Stack<List<VideoSegment>> _undo = new();
    private readonly Stack<List<VideoSegment>> _redo = new();

    public VideoTimeline(TimeSpan sourceDuration)
    {
        if (sourceDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(sourceDuration), "The video has no duration.");
        SourceDuration = sourceDuration;
        _segments = new() { new VideoSegment(TimeSpan.Zero, sourceDuration) };
    }

    public TimeSpan SourceDuration { get; }
    public IReadOnlyList<VideoSegment> Segments => _segments;
    public TimeSpan OutputDuration => TimeSpan.FromTicks(_segments.Sum(s => s.Duration.Ticks));
    public TimeSpan RemovedDuration => SourceDuration - OutputDuration;
    public bool IsEdited => _segments.Count != 1 || _segments[0].Start != TimeSpan.Zero || _segments[0].End != SourceDuration || _segments[0].Muted;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public event Action? Changed;

    /// <summary>Index of the kept segment containing <paramref name="t"/>, or -1 when t is in a removed part.</summary>
    public int IndexAt(TimeSpan t) => _segments.FindIndex(s => s.Contains(t));

    /// <summary>Where playback should be for source time <paramref name="t"/>: t itself if kept, else the start of the next kept segment, else null (past the end).</summary>
    public TimeSpan? PlayablePosition(TimeSpan t)
    {
        foreach (var s in _segments)
        {
            if (s.Contains(t)) return t;
            if (s.Start >= t) return s.Start;
        }
        return null;
    }

    /// <summary>Converts a source time to its position in the exported video.</summary>
    public TimeSpan ToOutputTime(TimeSpan source)
    {
        long ticks = 0;
        foreach (var s in _segments)
        {
            if (source >= s.End) { ticks += s.Duration.Ticks; continue; }
            if (source > s.Start) ticks += (source - s.Start).Ticks;
            break;
        }
        return TimeSpan.FromTicks(ticks);
    }

    /// <summary>Splits the segment under <paramref name="t"/> in two. Returns false if too close to an edge.</summary>
    public bool SplitAt(TimeSpan t)
    {
        int i = IndexAt(t);
        if (i < 0) return false;
        var s = _segments[i];
        if (t - s.Start < MinSegment || s.End - t < MinSegment) return false;
        Mutate(list =>
        {
            list[i] = s with { End = t };
            list.Insert(i + 1, s with { Start = t });
        });
        return true;
    }

    /// <summary>Removes a segment from the output. The last remaining segment cannot be removed.</summary>
    public bool Delete(int index)
    {
        if (index < 0 || index >= _segments.Count || _segments.Count == 1) return false;
        Mutate(list => list.RemoveAt(index));
        return true;
    }

    /// <summary>Moves a segment's start (trim in), clamped to the previous segment and a minimum length.</summary>
    public void SetStart(int index, TimeSpan start, bool record = true)
    {
        var s = _segments[index];
        var min = index > 0 ? _segments[index - 1].End : TimeSpan.Zero;
        var clamped = Clamp(start, min, s.End - MinSegment);
        if (clamped == s.Start) return;
        Mutate(list => list[index] = s with { Start = clamped }, record);
    }

    /// <summary>Moves a segment's end (trim out), clamped to the next segment and a minimum length.</summary>
    public void SetEnd(int index, TimeSpan end, bool record = true)
    {
        var s = _segments[index];
        var max = index < _segments.Count - 1 ? _segments[index + 1].Start : SourceDuration;
        var clamped = Clamp(end, s.Start + MinSegment, max);
        if (clamped == s.End) return;
        Mutate(list => list[index] = s with { End = clamped }, record);
    }

    public void ToggleMute(int index)
    {
        if (index < 0 || index >= _segments.Count) return;
        Mutate(list => list[index] = list[index] with { Muted = !list[index].Muted });
    }

    /// <summary>Mutes or unmutes every segment.</summary>
    public void SetAllMuted(bool muted)
    {
        if (_segments.All(s => s.Muted == muted)) return;
        Mutate(list => { for (int i = 0; i < list.Count; i++) list[i] = list[i] with { Muted = muted }; });
    }

    /// <summary>Back to the untouched source (undoable).</summary>
    public void Reset()
    {
        if (!IsEdited) return;
        Mutate(list => { list.Clear(); list.Add(new VideoSegment(TimeSpan.Zero, SourceDuration)); });
    }

    /// <summary>Records the current state so a series of non-recorded edits (a drag) becomes one undo step.</summary>
    public void Checkpoint()
    {
        _undo.Push(new List<VideoSegment>(_segments));
        _redo.Clear();
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(_segments);
        _segments = _undo.Pop();
        Changed?.Invoke();
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(_segments);
        _segments = _redo.Pop();
        Changed?.Invoke();
    }

    private void Mutate(Action<List<VideoSegment>> change, bool record = true)
    {
        if (record) Checkpoint();
        var next = new List<VideoSegment>(_segments);
        change(next);
        _segments = next;
        Changed?.Invoke();
    }

    private static TimeSpan Clamp(TimeSpan v, TimeSpan min, TimeSpan max) => v < min ? min : v > max ? max : v;
}
