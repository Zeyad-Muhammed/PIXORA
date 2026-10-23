using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PIXORA.App.Services;
using PIXORA.App.ViewModels;
using PIXORA.Core;
using PIXORA.Recording;

namespace PIXORA.App.Pages;

public sealed partial class HomePage : Page, IRefreshable
{
    private readonly HomeViewModel _vm = App.Get<HomeViewModel>();
    private readonly MediaLibraryService _library = App.Get<MediaLibraryService>();
    private readonly RecordingController _recording = App.Get<RecordingController>();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public HomePage()
    {
        InitializeComponent();
        ShotsGrid.ItemsSource = _vm.RecentScreenshots;
        RecList.ItemsSource = _vm.RecentRecordings;
        _timer.Tick += (_, _) => UpdateStatus();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        _library.Changed += OnLibraryChanged;
        _timer.Start();
        Refresh();
        await LoadAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _library.Changed -= OnLibraryChanged;
        _timer.Stop();
    }

    private async void OnLibraryChanged() => await LoadAsync();

    private async Task LoadAsync()
    {
        await _vm.LoadAsync();
        ShotCountText.Text = $"{_vm.RecentScreenshots.Count} of {_vm.ScreenshotCount}";
        RecCountText.Text = $"{_vm.RecordingCount} total";
        LibrarySummary.Text = $"Browse {_vm.ScreenshotCount + _vm.RecordingCount} saved captures and clips";
        ShotsEmpty.Visibility = _vm.RecentScreenshots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecEmpty.Visibility = _vm.RecentRecordings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MicText.Text = _vm.MicrophoneText;
    }

    public void Refresh()
    {
        var hk = App.Get<SettingsStore>().Current.Hotkeys;
        ShotKey.Text = Pretty(hk.RegionCapture);
        RecKey.Text = Pretty(hk.ToggleRecording);
        EditKey.Text = Pretty(hk.OpenEditor);
        StatusShortcut.Text = hk.RegionCapture;
        ShotsEmptyHint.Text = string.IsNullOrEmpty(hk.RegionCapture) ? "Use Capture Region above" : $"Press {hk.RegionCapture} or use Capture Region above";
        RecEmptyHint.Text = string.IsNullOrEmpty(hk.ToggleRecording) ? "Use Record Screen above" : $"Press {hk.ToggleRecording} to start recording";

        HotkeyList.Children.Clear();
        void Add(string key, string label)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            sp.Children.Add(new Border { Style = (Style)Application.Current.Resources["PxBadge"], Child = new TextBlock { Text = key, Style = (Style)Application.Current.Resources["PxBadgeText"] } });
            sp.Children.Add(new TextBlock { Text = label, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            HotkeyList.Children.Add(sp);
        }
        Add(hk.RegionCapture, "Region");
        Add(hk.WindowCapture, "Window");
        Add(hk.FullScreenCapture, "Full screen");
        Add(hk.ToggleRecording, "Record");
        Add(hk.OpenEditor, "Editor");
        var conflicts = App.Shell?.HotkeyConflicts ?? Array.Empty<string>();
        HotkeyWarning.Text = conflicts.Count > 0 ? $"In use by another app: {string.Join(", ", conflicts)}" : "";
        UpdateStatus();
    }

    private static string Pretty(string key) => string.IsNullOrWhiteSpace(key) ? "Not assigned" : key.Replace("+", " + ");

    private void UpdateStatus()
    {
        var state = _recording.State;
        bool rec = state is RecorderState.Recording or RecorderState.Paused;
        StatusText.Text = state switch
        {
            RecorderState.Recording => $"Recording {UiHelpers.FormatDuration(_recording.Elapsed.TotalSeconds)}",
            RecorderState.Paused => $"Paused at {UiHelpers.FormatDuration(_recording.Elapsed.TotalSeconds)}",
            RecorderState.Stopping => "Saving recording…",
            _ => ScreenRecorder.IsSupported ? "System ready" : "Recording unsupported",
        };
        StatusCaps.Text = rec ? "RECORDING IN PROGRESS" : "CAPTURE ENGINE READY";
        var brush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[rec ? "PxAccent" : "PxSuccess"];
        StatusDot.Fill = brush;
        StatusIcon.Foreground = brush;
        StatusIcon.Glyph = rec ? "" : "";
    }

    private async void Screenshot_Click(object sender, RoutedEventArgs e) => await App.Get<CaptureController>().CaptureAsync(CaptureMode.Region);
    private void Record_Click(object sender, RoutedEventArgs e) => App.MainWindow.Navigate(PageKind.Record);
    private void Editor_Click(object sender, RoutedEventArgs e) => App.MainWindow.Navigate(PageKind.Editor);
    private void Library_Click(object sender, RoutedEventArgs e) => App.MainWindow.Navigate(PageKind.Library);
    private void Recordings_Click(object sender, RoutedEventArgs e) => App.MainWindow.Navigate(PageKind.Library, LibraryCategory.Recordings);
    private void Settings_Click(object sender, RoutedEventArgs e) => App.MainWindow.Navigate(PageKind.Settings);

    private async void Quick_Click(object sender, RoutedEventArgs e)
    {
        var cap = App.Get<CaptureController>();
        switch ((sender as Button)?.Tag as string)
        {
            case "CaptureRegion": await cap.CaptureAsync(CaptureMode.Region); break;
            case "CaptureWindow": await cap.CaptureAsync(CaptureMode.Window); break;
            case "CaptureFull": await cap.CaptureAsync(CaptureMode.FullScreen); break;
            case "RecordScreen": await _recording.PickAndStartAsync(RecordingSourceKind.Monitor); break;
            case "RecordWindow": await _recording.PickAndStartAsync(RecordingSourceKind.Window); break;
            case "RecordRegion": await _recording.PickAndStartAsync(RecordingSourceKind.Region); break;
        }
    }

    private async void Shot_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not MediaCardViewModel card) return;
        await LibraryActions.OpenAsync(card.Item, XamlRoot);
    }

    private async void Rec_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is MediaCardViewModel card) await LibraryActions.OpenAsync(card.Item, XamlRoot);
    }
}
