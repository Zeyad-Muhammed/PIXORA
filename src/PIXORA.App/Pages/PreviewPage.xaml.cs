using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PIXORA.App.Services;
using PIXORA.Core;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace PIXORA.App.Pages;

/// <summary>Plays a recording and offers file actions. Playback, seeking and volume come from the system transport controls.</summary>
public sealed partial class PreviewPage : Page
{
    private readonly MediaLibraryService _library = App.Get<MediaLibraryService>();
    private MediaItem? _item;

    public PreviewPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is long id) _item = _library.Get(id);
        Load();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => ReleasePlayer();

    private void ReleasePlayer()
    {
        // Release the file handle so the recording can be renamed, moved or deleted.
        if (Player.MediaPlayer is { } mp) { mp.Pause(); mp.Source = null; }
        Player.SetMediaPlayer(null);
        Player.Source = null;
    }

    private void Load()
    {
        if (_item == null) { Title.Text = "Recording not found"; return; }
        Title.Text = _item.Name;
        bool exists = File.Exists(_item.Path);
        Subtitle.Text = exists ? UiHelpers.FormatRelative(_item.CreatedUtc) : "File missing";
        DurationText.Text = UiHelpers.FormatDuration(_item.DurationSeconds);
        ResText.Text = _item.Width > 0 ? $"{_item.Width}×{_item.Height}" : "Unknown";
        SizeText.Text = UiHelpers.FormatSize(exists ? new FileInfo(_item.Path).Length : _item.SizeBytes);
        DateText.Text = _item.CreatedUtc.ToLocalTime().ToString("f");
        PathText.Text = _item.Path;
        FavText.Text = _item.IsFavorite ? "Remove from favorites" : "Add to favorites";
        FavGlyph.Glyph = _item.IsFavorite ? "" : "";
        MissingState.Visibility = exists ? Visibility.Collapsed : Visibility.Visible;
        if (exists)
        {
            var player = new MediaPlayer { Source = MediaSource.CreateFromUri(new Uri(_item.Path)), Volume = 1.0 };
            player.MediaFailed += (_, a) => DispatcherQueue.TryEnqueue(() => App.MainWindow.ShowBanner("Playback failed", a.ErrorMessage, InfoBarSeverity.Error));
            Player.SetMediaPlayer(player);
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e) => App.MainWindow.Navigate(PageKind.Library, LibraryCategory.Recordings);

    private async void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (_item == null || !File.Exists(_item.Path)) return;
        var dest = await UiHelpers.PickSaveVideoAsync(Path.GetFileNameWithoutExtension(_item.Path));
        if (dest == null) return;
        if (string.Equals(Path.GetFullPath(dest), Path.GetFullPath(_item.Path), StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            await Task.Run(() => File.Copy(_item.Path, dest, true));
            App.MainWindow.ShowBanner("Saved", dest, InfoBarSeverity.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.MainWindow.ShowBanner("Save failed", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_item != null) await LibraryActions.ExportAsync(new[] { _item });
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (_item == null) return;
        ReleasePlayer();
        if (await LibraryActions.RenameAsync(_item, XamlRoot)) _item = _library.Get(_item.Id);
        Load();
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (_item != null) UiHelpers.RevealInExplorer(_item.Path);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_item != null) LibraryActions.Copy(new[] { _item });
    }

    private async void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (_item == null) return;
        await _library.SetFavoriteAsync(new[] { _item.Id }, !_item.IsFavorite);
        _item = _library.Get(_item.Id);
        FavText.Text = _item!.IsFavorite ? "Remove from favorites" : "Add to favorites";
        FavGlyph.Glyph = _item.IsFavorite ? "" : "";
    }

    private async void Trash_Click(object sender, RoutedEventArgs e)
    {
        if (_item == null) return;
        ReleasePlayer();
        await _library.TrashAsync(new[] { _item.Id });
        App.MainWindow.ShowBanner("Moved to Trash", $"{_item.Name} is still on disk and can be restored from Trash.", InfoBarSeverity.Success);
        App.MainWindow.Navigate(PageKind.Library, LibraryCategory.Recordings);
    }
}
