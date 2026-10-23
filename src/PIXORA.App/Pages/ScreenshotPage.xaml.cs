using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using PIXORA.App.Services;
using PIXORA.Capture;
using PIXORA.Core;

namespace PIXORA.App.Pages;

public sealed partial class ScreenshotPage : Page, IRefreshable
{
    private readonly SettingsStore _settings = App.Get<SettingsStore>();
    private readonly CaptureController _capture = App.Get<CaptureController>();
    private CaptureMode _mode = CaptureMode.Region;
    private bool _loading;

    public ScreenshotPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _capture.Captured += OnCaptured;
        _loading = true;
        var s = _settings.Current;
        var monitors = ScreenCapture.GetMonitors();
        MonitorBox.ItemsSource = monitors;
        MonitorBox.SelectedIndex = monitors.Count > 0 ? 0 : -1;
        FullDesc.Text = monitors.Count > 1 ? $"All {monitors.Count} displays in one image." : "The whole display.";
        DelayBox.SelectedItem = DelayBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == s.CaptureDelaySeconds.ToString()) ?? DelayBox.Items[0];
        FormatBox.SelectedItem = FormatBox.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == s.ScreenshotFormat.ToString());
        AutoSaveSwitch.IsOn = s.AutoSaveScreenshots;
        ClipboardSwitch.IsOn = s.CopyToClipboardAfterCapture;
        EditorSwitch.IsOn = s.OpenEditorAfterCapture;
        FolderText.Text = s.ScreenshotDir;
        _loading = false;
        Refresh();
        ShowLast(_capture.LastOutcome);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => _capture.Captured -= OnCaptured;

    public void Refresh()
    {
        var hk = _settings.Current.Hotkeys;
        KeyRegion.Text = hk.RegionCapture;
        KeyWindow.Text = hk.WindowCapture;
        KeyFull.Text = hk.FullScreenCapture;
    }

    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string tag }) return;
        _mode = Enum.Parse<CaptureMode>(tag);
        foreach (var t in new[] { ModeRegion, ModeWindow, ModeFull, ModeMonitor }) t.IsChecked = (string)t.Tag == tag;
        MonitorBox.IsEnabled = _mode == CaptureMode.Monitor;
        CaptureLabel.Text = _mode switch
        {
            CaptureMode.Window => "Capture window",
            CaptureMode.FullScreen => "Capture full screen",
            CaptureMode.Monitor => "Capture display",
            _ => "Capture region",
        };
    }

    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = _settings.Clone();
        s.CaptureDelaySeconds = int.Parse((string)((ComboBoxItem)DelayBox.SelectedItem).Tag);
        s.ScreenshotFormat = Enum.Parse<ImageFormat>((string)((ComboBoxItem)FormatBox.SelectedItem).Tag);
        s.AutoSaveScreenshots = AutoSaveSwitch.IsOn;
        s.CopyToClipboardAfterCapture = ClipboardSwitch.IsOn;
        s.OpenEditorAfterCapture = EditorSwitch.IsOn;
        if (!s.AutoSaveScreenshots && !s.CopyToClipboardAfterCapture && !s.OpenEditorAfterCapture)
        {
            // Every capture must go somewhere; keep the editor on so nothing is silently lost.
            s.OpenEditorAfterCapture = true;
            _loading = true; EditorSwitch.IsOn = true; _loading = false;
        }
        Save(s);
    }

    private void Option_SelectionChanged(object sender, SelectionChangedEventArgs e) => Option_Changed(sender, e);

    private void Save(AppSettings s)
    {
        try { _settings.Save(s); }
        catch (SettingsException ex) { App.MainWindow.ShowBanner("Setting not saved", ex.Message, InfoBarSeverity.Error); }
    }

    private async void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = await UiHelpers.PickFolderAsync();
        if (folder == null) return;
        var problem = SettingsValidator.ProbeWritable(folder);
        if (problem != null) { App.MainWindow.ShowBanner("Folder not writable", problem, InfoBarSeverity.Error); return; }
        var s = _settings.Clone();
        s.ScreenshotDir = folder;
        Save(s);
        FolderText.Text = folder;
        _ = App.Get<MediaLibraryService>().RefreshAsync();
    }

    private async void Capture_Click(object sender, RoutedEventArgs e)
    {
        CaptureButton.IsEnabled = false;
        try { await _capture.CaptureAsync(_mode, MonitorBox.SelectedItem as MonitorInfo); }
        finally { CaptureButton.IsEnabled = true; }
    }

    private async void Paste_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var bmp = ClipboardService.GetImage();
            if (bmp == null) { App.MainWindow.ShowBanner("No image on the clipboard", "Copy an image first, then try again.", InfoBarSeverity.Warning); return; }
            if (!await App.MainWindow.ConfirmDiscardEditorChangesAsync()) { bmp.Dispose(); return; }
            App.Get<EditorSession>().OpenBitmap(bmp, null);
            App.MainWindow.Navigate(PageKind.Editor);
        }
        catch (CaptureException ex) { App.MainWindow.ShowBanner("Clipboard unavailable", ex.Message, InfoBarSeverity.Error); }
    }

    private void OnCaptured(CaptureOutcome o) => ShowLast(o);

    private void ShowLast(CaptureOutcome? o)
    {
        if (o == null) return;
        LastEmpty.Visibility = Visibility.Collapsed;
        LastName.Text = o.SavedPath != null ? Path.GetFileName(o.SavedPath) : "Unsaved capture";
        LastInfo.Text = $"{o.Width}×{o.Height}" + (o.CopiedToClipboard ? " · copied to clipboard" : "");
        var item = o.SavedPath != null ? App.Get<MediaLibraryService>().GetByPath(o.SavedPath) : null;
        LastImage.Source = UiHelpers.LoadThumb(item?.ThumbPath ?? o.SavedPath, 600);
        LastActions.Visibility = o.SavedPath != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void LastEdit_Click(object sender, RoutedEventArgs e)
    {
        var p = _capture.LastOutcome?.SavedPath;
        var item = p != null ? App.Get<MediaLibraryService>().GetByPath(p) : null;
        if (item != null) await LibraryActions.OpenAsync(item, XamlRoot);
    }

    private void LastCopy_Click(object sender, RoutedEventArgs e)
    {
        var p = _capture.LastOutcome?.SavedPath;
        var item = p != null ? App.Get<MediaLibraryService>().GetByPath(p) : null;
        if (item != null) LibraryActions.Copy(new[] { item });
    }

    private void LastReveal_Click(object sender, RoutedEventArgs e)
    {
        if (_capture.LastOutcome?.SavedPath is { } p) UiHelpers.RevealInExplorer(p);
    }
}
