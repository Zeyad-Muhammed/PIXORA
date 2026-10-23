using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PIXORA.App.Services;
using PIXORA.Core;

namespace PIXORA.App.ViewModels;

public sealed partial class LibraryViewModel : ObservableObject
{
    private readonly MediaLibraryService _library;
    private int _loadVersion;

    public LibraryViewModel(MediaLibraryService library) => _library = library;

    public ObservableCollection<MediaCardViewModel> Items { get; } = new();

    [ObservableProperty] private LibraryCategory _category = LibraryCategory.All;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private LibrarySort _sort = LibrarySort.DateNewest;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private int _countAll, _countShots, _countRecs, _countFav, _countTrash;
    [ObservableProperty] private int _missingCount;

    public async Task LoadAsync()
    {
        int version = ++_loadVersion;
        IsLoading = true;
        try
        {
            var items = await _library.QueryAsync(Category, Search, Sort);
            if (version != _loadVersion) return; // a newer query superseded this one
            Items.Clear();
            foreach (var i in items) Items.Add(new MediaCardViewModel(i));
            MissingCount = items.Count(i => i.IsMissing);
            CountAll = await _library.CountAsync(LibraryCategory.All);
            CountShots = await _library.CountAsync(LibraryCategory.Screenshots);
            CountRecs = await _library.CountAsync(LibraryCategory.Recordings);
            CountFav = await _library.CountAsync(LibraryCategory.Favorites);
            CountTrash = await _library.CountAsync(LibraryCategory.Trash);
        }
        finally { if (version == _loadVersion) IsLoading = false; }
    }
}
