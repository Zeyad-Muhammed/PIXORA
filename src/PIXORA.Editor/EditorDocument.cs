using SkiaSharp;

namespace PIXORA.Editor;

/// <summary>
/// An image plus its editable annotation list, crop rectangle and undo/redo history.
/// The base bitmap is never modified; everything is applied at render/export time.
/// </summary>
public sealed class EditorDocument : IDisposable
{
    private sealed record Snapshot(List<Annotation> Items, SKRectI? Crop);

    private readonly Stack<Snapshot> _undo = new();
    private readonly Stack<Snapshot> _redo = new();
    private const int MaxHistory = 100;

    public EditorDocument(SKBitmap baseImage, string? filePath = null)
    {
        Base = baseImage;
        FilePath = filePath;
    }

    public SKBitmap Base { get; }
    public List<Annotation> Annotations { get; private set; } = new();
    public SKRectI? Crop { get; private set; }
    public string? FilePath { get; set; }
    public bool IsDirty { get; private set; }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Raised after any change to annotations, crop or history.</summary>
    public event Action? Changed;

    public int Width => Crop?.Width ?? Base.Width;
    public int Height => Crop?.Height ?? Base.Height;

    public static EditorDocument Open(string path)
    {
        using var data = SKData.Create(path) ?? throw new IOException($"Cannot read \"{path}\".");
        var bmp = SKBitmap.Decode(data) ?? throw new InvalidDataException($"\"{Path.GetFileName(path)}\" is not a supported image.");
        if (bmp.ColorType != SKColorType.Bgra8888 || bmp.AlphaType != SKAlphaType.Premul)
        {
            var converted = bmp.Copy(SKColorType.Bgra8888);
            bmp.Dispose();
            bmp = converted ?? throw new InvalidDataException("Unsupported pixel format.");
        }
        return new EditorDocument(bmp, path);
    }

    /// <summary>Records the current state for undo. Call before mutating annotations or crop.</summary>
    public void Checkpoint()
    {
        _undo.Push(Capture());
        if (_undo.Count > MaxHistory)
        {
            var arr = _undo.Reverse().Skip(1).ToArray();
            _undo.Clear();
            foreach (var s in arr) _undo.Push(s);
        }
        _redo.Clear();
        IsDirty = true;
    }

    /// <summary>Drops the most recent checkpoint (used when a gesture produced no change).</summary>
    public void DiscardCheckpoint()
    {
        if (_undo.Count > 0) _undo.Pop();
    }

    public void Add(Annotation a)
    {
        Checkpoint();
        Annotations.Add(a);
        Changed?.Invoke();
    }

    public void Remove(Annotation a)
    {
        Checkpoint();
        Annotations.RemoveAll(x => x.Id == a.Id);
        Changed?.Invoke();
    }

    public void SetCrop(SKRectI? crop)
    {
        Checkpoint();
        Crop = crop is { } c ? SKRectI.Intersect(c, new SKRectI(0, 0, Base.Width, Base.Height)) : null;
        if (Crop is { Width: < 1 } or { Height: < 1 }) Crop = null;
        Changed?.Invoke();
    }

    public void NotifyChanged() => Changed?.Invoke();

    public int NextStepNumber() =>
        Annotations.OfType<StepAnnotation>().Select(s => s.Number).DefaultIfEmpty(0).Max() + 1;

    public Annotation? HitTest(SKPoint p, float tolerance)
    {
        for (int i = Annotations.Count - 1; i >= 0; i--)
            if (Annotations[i].HitTest(p, tolerance)) return Annotations[i];
        return null;
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(Capture());
        Restore(_undo.Pop());
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(Capture());
        Restore(_redo.Pop());
    }

    public void MarkSaved() => IsDirty = false;

    private Snapshot Capture() => new(Annotations.Select(a => a.Clone()).ToList(), Crop);

    private void Restore(Snapshot s)
    {
        Annotations = s.Items;
        Crop = s.Crop;
        IsDirty = true;
        Changed?.Invoke();
    }

    public void Dispose() => Base.Dispose();
}
