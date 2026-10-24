using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using PIXORA.App.Services;
using PIXORA.App.ViewModels;
using PIXORA.Core;
using Windows.System;

namespace PIXORA.App.Pages;

public sealed partial class LibraryPage : Page
{
    private readonly LibraryViewModel _vm = App.Get<LibraryViewModel>();
    private readonly MediaLibraryService _library = App.Get<MediaLibraryService>();
    private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _gridMode = true;

    public LibraryPage()
    {
        InitializeComponent();
        GridViewItems.ItemsSource = _vm.Items;
        ListViewItems.ItemsSource = _vm.Items;
        _searchDebounce.Tick += async (_, _) => { _searchDebounce.Stop(); await ReloadAsync(); };
    }

    private ListViewBase ActiveList => _gridMode ? GridViewItems : ListViewItems;

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is LibraryCategory cat) _vm.Category = cat;
        _library.Changed += OnLibraryChanged;
        SyncCategoryButtons();
        await ReloadAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => _library.Changed -= OnLibraryChanged;

    private async void OnLibraryChanged() => await ReloadAsync();

    private async Task ReloadAsync()
    {
        var keep = Selected().Select(s => s.Id).ToHashSet();
        Busy.IsActive = true;
        await _vm.LoadAsync();
        Busy.IsActive = false;
        CountAll.Text = _vm.CountAll.ToString();
        CountShots.Text = _vm.CountShots.ToString();
        CountRecs.Text = _vm.CountRecs.ToString();
        CountFav.Text = _vm.CountFav.ToString();
        CountTrash.Text = _vm.CountTrash.ToString();
        Heading.Text = _vm.Category switch
        {
            LibraryCategory.Screenshots => "Screenshots", LibraryCategory.Recordings => "Recordings",
            LibraryCategory.Favorites => "Favorites", LibraryCategory.Trash => "Trash", _ => "All files",
        };
        long total = _vm.Items.Sum(i => i.Item.SizeBytes);
        SubHeading.Text = $"{_vm.Items.Count} item(s) · {UiHelpers.FormatSize(total)}" + (string.IsNullOrWhiteSpace(_vm.Search) ? "" : $" matching “{_vm.Search}”");
        MissingBar.IsOpen = _vm.MissingCount > 0;
        EmptyState.Visibility = _vm.Items.Count == 0 && !_vm.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = string.IsNullOrWhiteSpace(_vm.Search) ? "Nothing here yet" : "No matches";
        EmptyText.Text = !string.IsNullOrWhiteSpace(_vm.Search) ? "Try a different search." : _vm.Category switch
        {
            LibraryCategory.Trash => "Items you move to Trash appear here.",
            LibraryCategory.Favorites => "Mark captures as favorites to collect them here.",
            _ => "Screenshots and recordings saved by PIXORA (and files already in your capture folders) appear here.",
        };
        foreach (var it in _vm.Items.Where(i => keep.Contains(i.Id))) ActiveList.SelectedItems.Add(it);
        UpdateActionBar();
    }

    private void SyncCategoryButtons()
    {
        foreach (var b in new[] { CatAll, CatShots, CatRecs, CatFav, CatTrash }) b.IsChecked = (string)b.Tag == _vm.Category.ToString();
    }

    private async void Category_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string tag }) _vm.Category = Enum.Parse<LibraryCategory>(tag);
        SyncCategoryButtons();
        await ReloadAsync();
    }

    private void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        _vm.Search = sender.Text;
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private async void Sort_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SortBox.SelectedItem is ComboBoxItem { Tag: string tag }) _vm.Sort = Enum.Parse<LibrarySort>(tag);
        if (IsLoaded) await ReloadAsync();
    }

    private void ViewMode_Click(object sender, RoutedEventArgs e)
    {
        _gridMode = (string)((FrameworkElement)sender).Tag == "Grid";
        GridToggle.IsChecked = _gridMode;
        ListToggle.IsChecked = !_gridMode;
        GridViewItems.Visibility = _gridMode ? Visibility.Visible : Visibility.Collapsed;
        ListViewItems.Visibility = _gridMode ? Visibility.Collapsed : Visibility.Visible;
        UpdateActionBar();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        Busy.IsActive = true;
        var s = await _library.RefreshAsync();
        Busy.IsActive = false;
        App.MainWindow.ShowBanner("Library refreshed", $"{s.Added} added · {s.MarkedMissing} newly missing · {s.Restored} found again", InfoBarSeverity.Success);
    }

    private List<MediaItem> Selected() => ActiveList.SelectedItems.OfType<MediaCardViewModel>().Select(c => c.Item).ToList();

    private void Items_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateActionBar();

    private void UpdateActionBar()
    {
        var sel = Selected();
        bool trash = _vm.Category == LibraryCategory.Trash;
        ActionBar.Visibility = sel.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectionText.Text = sel.Count == 1 ? sel[0].Name : $"{sel.Count} selected";
        ActOpen.Visibility = sel.Count == 1 && !trash ? Visibility.Visible : Visibility.Collapsed;
        ActEditVideo.Visibility = sel.Count == 1 && !trash && sel[0].Kind == MediaKind.Recording && !sel[0].IsMissing ? Visibility.Visible : Visibility.Collapsed;
        ActRename.Visibility = sel.Count == 1 && !trash ? Visibility.Visible : Visibility.Collapsed;
        ActReveal.Visibility = sel.Count == 1 ? Visibility.Visible : Visibility.Collapsed;
        ActFav.Visibility = trash ? Visibility.Collapsed : Visibility.Visible;
        ActTrash.Visibility = trash ? Visibility.Collapsed : Visibility.Visible;
        ActRestore.Visibility = trash ? Visibility.Visible : Visibility.Collapsed;
        ActDelete.Visibility = trash ? Visibility.Visible : Visibility.Collapsed;
        ActRemove.Visibility = trash || sel.Any(s => s.IsMissing) ? Visibility.Visible : Visibility.Collapsed;
        bool allFav = sel.Count > 0 && sel.All(s => s.IsFavorite);
        FavText.Text = allFav ? "Unfavorite" : "Favorite";
        FavGlyph.Glyph = allFav ? "" : "";
    }

    private async void Items_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is MediaCardViewModel card && _vm.Category != LibraryCategory.Trash)
            await LibraryActions.OpenAsync(card.Item, XamlRoot);
    }

    private void Items_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not MediaCardViewModel card) return;
        if (!ActiveList.SelectedItems.Contains(card)) { ActiveList.SelectedItems.Clear(); ActiveList.SelectedItems.Add(card); }
        var menu = new MenuFlyout();
        void Item(string text, string glyph, RoutedEventHandler h, bool enabled = true)
        {
            var mi = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
            mi.Click += h;
            menu.Items.Add(mi);
        }
        var sel = Selected();
        if (_vm.Category == LibraryCategory.Trash)
        {
            Item("Restore", "", Restore_Click);
            Item("Remove from library", "", RemoveEntry_Click);
            Item("Delete permanently", "", Delete_Click);
        }
        else
        {
            Item("Open", "", Open_Click, sel.Count == 1);
            if (sel.Count == 1 && sel[0].Kind == MediaKind.Recording && !sel[0].IsMissing) Item("Edit video", "", EditVideo_Click);
            Item("Rename", "", Rename_Click, sel.Count == 1);
            Item(sel.All(s => s.IsFavorite) ? "Unfavorite" : "Favorite", "", Favorite_Click);
            Item("Copy", "", Copy_Click);
            Item("Export…", "", Export_Click);
            Item("Show in folder", "", Reveal_Click, sel.Count == 1);
            menu.Items.Add(new MenuFlyoutSeparator());
            if (sel.Any(s => s.IsMissing)) Item("Remove from library", "", RemoveEntry_Click);
            Item("Move to Trash", "", Trash_Click);
        }
        menu.ShowAt((FrameworkElement)e.OriginalSource, e.GetPosition((FrameworkElement)e.OriginalSource));
        e.Handled = true;
    }

    private void Items_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Delete && Selected().Count > 0)
        {
            if (_vm.Category == LibraryCategory.Trash) Delete_Click(sender, e); else Trash_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Enter && Selected().Count == 1) { Open_Click(sender, e); e.Handled = true; }
        else if (e.Key == VirtualKey.F2 && Selected().Count == 1) { Rename_Click(sender, e); e.Handled = true; }
    }

    private void EditVideo_Click(object sender, RoutedEventArgs e)
    {
        var s = Selected();
        if (s.Count == 1 && s[0].Kind == MediaKind.Recording) App.MainWindow.Navigate(PageKind.VideoEditor, s[0].Id);
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var s = Selected();
        if (s.Count == 1) await LibraryActions.OpenAsync(s[0], XamlRoot);
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        var s = Selected();
        if (s.Count == 1) await LibraryActions.RenameAsync(s[0], XamlRoot);
    }

    private async void Favorite_Click(object sender, RoutedEventArgs e)
    {
        var s = Selected();
        await _library.SetFavoriteAsync(s.Select(i => i.Id), !s.All(i => i.IsFavorite));
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => LibraryActions.Copy(Selected());
    private async void Export_Click(object sender, RoutedEventArgs e) => await LibraryActions.ExportAsync(Selected());

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        var s = Selected();
        if (s.Count == 1) UiHelpers.RevealInExplorer(s[0].Path);
    }

    private async void Trash_Click(object sender, RoutedEventArgs e)
    {
        var s = Selected();
        if (s.Count == 0) return;
        await _library.TrashAsync(s.Select(i => i.Id));
        App.MainWindow.ShowBanner("Moved to Trash", $"{s.Count} item(s). Files are still on disk; restore them from Trash any time.", InfoBarSeverity.Success);
    }

    private async void Restore_Click(object sender, RoutedEventArgs e) => await _library.RestoreAsync(Selected().Select(i => i.Id));

    private async void RemoveEntry_Click(object sender, RoutedEventArgs e)
    {
        var s = Selected();
        if (s.Count == 0) return;
        var r = await UiHelpers.ShowDialogAsync(XamlRoot, "Remove from library?", $"{s.Count} entr{(s.Count == 1 ? "y" : "ies")} will be removed from PIXORA's library. Files on disk are not deleted.", "Remove");
        if (r == ContentDialogResult.Primary) await _library.RemoveEntriesAsync(s.Select(i => i.Id));
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var s = Selected();
        if (s.Count == 0) return;
        var r = await UiHelpers.ShowDialogAsync(XamlRoot, "Delete permanently?",
            $"{s.Count} file(s) will be moved to the Windows Recycle Bin and removed from the library.", "Delete", destructive: true);
        if (r != ContentDialogResult.Primary) return;
        var failed = await _library.DeletePermanentlyAsync(s.Select(i => i.Id));
        if (failed.Count > 0) App.MainWindow.ShowBanner("Some files could not be deleted", string.Join(", ", failed.Take(5)), InfoBarSeverity.Error);
    }
}
