using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using PIXORA.App.Services;
using PIXORA.Core;

namespace PIXORA.App.ViewModels;

/// <summary>Display wrapper for one library item (used by Home, Library and Preview).</summary>
public sealed partial class MediaCardViewModel : ObservableObject
{
    public MediaCardViewModel(MediaItem item) => Item = item;

    public MediaItem Item { get; }
    public long Id => Item.Id;
    public string Name => Item.Name;
    public bool IsRecording => Item.Kind == MediaKind.Recording;
    public string KindBadge => Path.GetExtension(Item.Path).TrimStart('.').ToUpperInvariant();
    public string When => UiHelpers.FormatRelative(Item.CreatedUtc);
    public string DateText => Item.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string SizeText => UiHelpers.FormatSize(Item.SizeBytes);
    public string Dimensions => Item.Width > 0 ? $"{Item.Width}×{Item.Height}" : "";
    public string DurationText => IsRecording ? UiHelpers.FormatDuration(Item.DurationSeconds) : "";
    public string TypeText => IsRecording ? "Recording" : "Screenshot";
    public string Status => Item.IsMissing ? "File missing" : Item.IsFavorite ? "Favorite" : IsRecording ? $"{DurationText} · {Dimensions}" : Dimensions;
    public Visibility MissingVisibility => Item.IsMissing ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FavoriteVisibility => Item.IsFavorite ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DurationVisibility => IsRecording ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PlaceholderVisibility => Thumb == null ? Visibility.Visible : Visibility.Collapsed;
    public string PlaceholderGlyph => IsRecording ? "" : "";

    private BitmapImage? _thumb;
    private bool _thumbLoaded;
    public BitmapImage? Thumb
    {
        get
        {
            if (!_thumbLoaded) { _thumbLoaded = true; _thumb = UiHelpers.LoadThumb(Item.ThumbPath); }
            return _thumb;
        }
    }
}
