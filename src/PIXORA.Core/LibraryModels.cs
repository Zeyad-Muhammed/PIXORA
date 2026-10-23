namespace PIXORA.Core;

public sealed record MediaItem
{
    public long Id { get; init; }
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public MediaKind Kind { get; init; }
    public long SizeBytes { get; init; }
    public DateTime CreatedUtc { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double DurationSeconds { get; init; }
    public bool IsFavorite { get; init; }
    public bool IsTrashed { get; init; }
    public bool IsMissing { get; init; }
    public string? ThumbPath { get; init; }
}
