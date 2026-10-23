using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PIXORA.App.Services;
using PIXORA.Core;
using PIXORA.Recording;

namespace PIXORA.App.ViewModels;

public sealed partial class HomeViewModel : ObservableObject
{
    private readonly MediaLibraryService _library;
    private readonly SettingsStore _settings;

    public HomeViewModel(MediaLibraryService library, SettingsStore settings)
    {
        _library = library;
        _settings = settings;
    }

    public ObservableCollection<MediaCardViewModel> RecentScreenshots { get; } = new();
    public ObservableCollection<MediaCardViewModel> RecentRecordings { get; } = new();

    [ObservableProperty] private int _screenshotCount;
    [ObservableProperty] private int _recordingCount;
    [ObservableProperty] private string _microphoneText = "";

    public HotkeySettings Hotkeys => _settings.Current.Hotkeys;

    public async Task LoadAsync()
    {
        var shots = await _library.QueryAsync(LibraryCategory.Screenshots, null, LibrarySort.DateNewest);
        var recs = await _library.QueryAsync(LibraryCategory.Recordings, null, LibrarySort.DateNewest);
        ScreenshotCount = shots.Count;
        RecordingCount = recs.Count;
        RecentScreenshots.Clear();
        foreach (var s in shots.Take(4)) RecentScreenshots.Add(new MediaCardViewModel(s));
        RecentRecordings.Clear();
        foreach (var r in recs.Take(3)) RecentRecordings.Add(new MediaCardViewModel(r));

        var s0 = _settings.Current;
        var mics = await Task.Run(AudioDevices.GetMicrophones);
        var chosen = mics.FirstOrDefault(m => m.Id == s0.MicrophoneDeviceId) ?? mics.FirstOrDefault(m => m.IsDefault);
        MicrophoneText = chosen == null ? "No microphone detected" :
            s0.RecordMicrophone ? $"Microphone: {chosen.Name}" : $"Microphone off · {chosen.Name} available";
        OnPropertyChanged(nameof(Hotkeys));
    }
}
