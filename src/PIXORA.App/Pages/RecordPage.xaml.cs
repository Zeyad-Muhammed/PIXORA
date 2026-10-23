using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using PIXORA.App.Services;
using PIXORA.App.Views;
using PIXORA.Capture;
using PIXORA.Core;
using PIXORA.Recording;
using SkiaSharp;

namespace PIXORA.App.Pages;

public sealed partial class RecordPage : Page, IRefreshable
{
    private readonly SettingsStore _settings = App.Get<SettingsStore>();
    private readonly RecordingController _rec = App.Get<RecordingController>();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _saveDebounce = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private RecordingSourceKind _kind = RecordingSourceKind.Monitor;
    private IntPtr _window;
    private string? _windowTitle;
    private RECT _region;
    private bool _loading;

    public RecordPage()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => UpdateLive();
        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); SaveSettings(); };
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        _loading = true;
        var s = _settings.Current;
        var monitors = ScreenCapture.GetMonitors();
        MonitorBox.ItemsSource = monitors;
        MonitorBox.SelectedIndex = monitors.Count > 0 ? 0 : -1;
        Select(FpsBox, s.RecordingFrameRate.ToString());
        Select(QualityBox, s.RecordingQuality.ToString());
        Select(ResBox, s.RecordingHeight.ToString());
        CursorBox.IsChecked = s.CaptureCursor;
        foreach (var d in new[] { Delay0, Delay3, Delay5 }) d.IsChecked = (string)d.Tag == s.RecordingCountdownSeconds.ToString();
        MicSwitch.IsOn = s.RecordMicrophone;
        SysSwitch.IsOn = s.RecordSystemAudio;
        MicVolume.Value = s.MicrophoneVolume * 100;
        SysVolume.Value = s.SystemAudioVolume * 100;
        SpeakerName.Text = await Task.Run(AudioDevices.DefaultSpeakersName) ?? "No playback device found";
        var mics = await Task.Run(AudioDevices.GetMicrophones);
        MicBox.ItemsSource = mics;
        MicBox.SelectedItem = mics.FirstOrDefault(m => m.Id == s.MicrophoneDeviceId) ?? mics.FirstOrDefault(m => m.IsDefault) ?? mics.FirstOrDefault();
        MicNote.Text = mics.Count == 0 ? "No microphone detected. Check Windows Settings > Privacy > Microphone." : "Level meter is live while recording.";
        _loading = false;
        UpdateVolumeLabels();

        _rec.StateChanged += OnState;
        _timer.Start();
        Refresh();
        UpdateTarget();
        UpdateLive();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _rec.StateChanged -= OnState;
        _timer.Stop();
        if (_saveDebounce.IsEnabled) { _saveDebounce.Stop(); SaveSettings(); }
    }

    private static void Select(ComboBox box, string tag) =>
        box.SelectedItem = box.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag) ?? box.Items[0];

    public void Refresh()
    {
        var hk = _settings.Current.Hotkeys.ToggleRecording;
        RecKey.Text = string.IsNullOrEmpty(hk) ? "No shortcut" : hk;
        RecHelp.Text = ScreenRecorder.IsSupported
            ? "PIXORA minimises itself; a small control bar (hidden from the recording) lets you pause or stop."
            : "Screen recording requires Windows 10 version 1903 or later.";
        RecButton.IsEnabled = ScreenRecorder.IsSupported;
    }

    // ---- source --------------------------------------------------------------------------------------------

    private async void Source_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string tag }) return;
        var kind = Enum.Parse<RecordingSourceKind>(tag);
        if (kind == RecordingSourceKind.Window) await PickAsync(SelectMode.Window);
        else if (kind == RecordingSourceKind.Region) await PickAsync(SelectMode.Region);
        else { _kind = RecordingSourceKind.Monitor; UpdateTarget(); }
        SyncSourceButtons();
    }

    private void SyncSourceButtons()
    {
        SrcScreen.IsChecked = _kind == RecordingSourceKind.Monitor;
        SrcWindow.IsChecked = _kind == RecordingSourceKind.Window;
        SrcRegion.IsChecked = _kind == RecordingSourceKind.Region;
    }

    private async void PickWindow_Click(object sender, RoutedEventArgs e) { await PickAsync(SelectMode.Window); SyncSourceButtons(); }
    private async void PickRegion_Click(object sender, RoutedEventArgs e) { await PickAsync(SelectMode.Region); SyncSourceButtons(); }

    private async Task PickAsync(SelectMode mode)
    {
        if (_rec.IsActive) return;
        var main = App.MainWindow;
        main.HideForCapture();
        await Task.Delay(250);
        try
        {
            using var sel = await ScreenSelector.SelectAsync(mode, mode == SelectMode.Window ? "Click the window to record" : "Drag to select the area to record", new[] { WindowChrome.Handle(main) });
            if (sel == null) return;
            if (mode == SelectMode.Window) { _kind = RecordingSourceKind.Window; _window = sel.Result.Window; _windowTitle = sel.Result.WindowTitle; }
            else { _kind = RecordingSourceKind.Region; _region = sel.Result.Rect; }
        }
        catch (CaptureException ex) { main.ShowBanner("Selection failed", ex.Message, InfoBarSeverity.Error); }
        finally
        {
            main.ShowAndActivate();
            UpdateTarget();
        }
    }

    private void Monitor_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _kind = RecordingSourceKind.Monitor;
        SyncSourceButtons();
        UpdateTarget();
    }

    private void RefreshPreview_Click(object sender, RoutedEventArgs e) => UpdateTarget();

    private MonitorInfo? SelectedMonitor => MonitorBox.SelectedItem as MonitorInfo;

    private RecordingSource? CurrentSource() => _kind switch
    {
        RecordingSourceKind.Window when _window != IntPtr.Zero => RecordingSource.ForWindow(_window),
        RecordingSourceKind.Region when _region.Width > 1 => RecordingSource.ForRegion(_region),
        RecordingSourceKind.Monitor when SelectedMonitor != null => RecordingSource.ForMonitor(SelectedMonitor),
        _ => null,
    };

    private void UpdateTarget()
    {
        var m = SelectedMonitor;
        SKBitmap? snap = null;
        try
        {
            switch (_kind)
            {
                case RecordingSourceKind.Window:
                    TargetGlyph.Glyph = "";
                    TargetTitle.Text = $"Target: {_windowTitle}";
                    TargetDetail.Text = "Follows this window even when it moves or is partly covered. Recording stops if the window closes.";
                    SourceTitle.Text = _windowTitle ?? "Window";
                    SourceBadge.Text = "WINDOW";
                    try { snap = ScreenCapture.CaptureWindow(_window); } catch (CaptureException) { }
                    break;
                case RecordingSourceKind.Region:
                    TargetGlyph.Glyph = "";
                    TargetTitle.Text = $"Target: {_region.Width}×{_region.Height} region";
                    TargetDetail.Text = $"Area locked to screen coordinates ({_region.Left}, {_region.Top}, {_region.Right}, {_region.Bottom}).";
                    SourceTitle.Text = $"Region {_region.Width}×{_region.Height}";
                    SourceBadge.Text = "REGION";
                    snap = ScreenCapture.CaptureRect(_region.Left, _region.Top, _region.Width, _region.Height);
                    break;
                default:
                    TargetGlyph.Glyph = "";
                    if (m == null) { TargetTitle.Text = "No display found"; break; }
                    TargetTitle.Text = $"Target: {(m.IsPrimary ? "Primary display" : "Display")} {m.Width}×{m.Height}";
                    TargetDetail.Text = $"Whole display at {m.DpiScale * 100:0}% scaling. Pick a window or region instead to record less.";
                    SourceTitle.Text = $"Display {m.Width}×{m.Height}";
                    SourceBadge.Text = m.IsPrimary ? "PRIMARY" : "SECONDARY";
                    snap = ScreenCapture.CaptureMonitor(m);
                    break;
            }
            if (snap != null)
            {
                using var small = snap.Resize(new SKImageInfo(Math.Max(1, snap.Width / 3), Math.Max(1, snap.Height / 3)), SKSamplingOptions.Default);
                PreviewImage.Source = UiHelpers.ToWriteable(small ?? snap);
            }
            else PreviewImage.Source = null;
        }
        catch (CaptureException) { PreviewImage.Source = null; }
        finally { snap?.Dispose(); }
    }

    // ---- settings ------------------------------------------------------------------------------------------

    private void Setting_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_loading) SaveSettings(); }
    private void Setting_Toggled(object sender, RoutedEventArgs e) { if (!_loading) SaveSettings(); }
    private void Setting_Click(object sender, RoutedEventArgs e) { if (!_loading) SaveSettings(); }

    private void Delay_Click(object sender, RoutedEventArgs e)
    {
        foreach (var d in new[] { Delay0, Delay3, Delay5 }) d.IsChecked = ReferenceEquals(d, sender);
        SaveSettings();
    }

    private void Volume_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        UpdateVolumeLabels();
        if (ReferenceEquals(sender, MicVolume)) _rec.SetMicrophoneVolume(MicVolume.Value / 100);
        else _rec.SetSystemVolume(SysVolume.Value / 100);
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    private void UpdateVolumeLabels()
    {
        MicVolumeText.Text = $"{MicVolume.Value:0}%";
        SysVolumeText.Text = $"{SysVolume.Value:0}%";
    }

    private void SaveSettings()
    {
        if (_loading) return;
        var s = _settings.Clone();
        s.RecordingFrameRate = int.Parse((string)((ComboBoxItem)FpsBox.SelectedItem).Tag);
        s.RecordingQuality = Enum.Parse<VideoQuality>((string)((ComboBoxItem)QualityBox.SelectedItem).Tag);
        s.RecordingHeight = int.Parse((string)((ComboBoxItem)ResBox.SelectedItem).Tag);
        s.CaptureCursor = CursorBox.IsChecked == true;
        s.RecordingCountdownSeconds = int.Parse((string)(new[] { Delay0, Delay3, Delay5 }.FirstOrDefault(d => d.IsChecked == true)?.Tag ?? "0"));
        s.RecordMicrophone = MicSwitch.IsOn;
        s.RecordSystemAudio = SysSwitch.IsOn;
        s.MicrophoneDeviceId = (MicBox.SelectedItem as AudioDeviceInfo)?.Id;
        s.MicrophoneVolume = MicVolume.Value / 100;
        s.SystemAudioVolume = SysVolume.Value / 100;
        try { _settings.Save(s); }
        catch (SettingsException ex) { App.MainWindow.ShowBanner("Setting not saved", ex.Message, InfoBarSeverity.Error); }
    }

    // ---- recording -----------------------------------------------------------------------------------------

    private async void Rec_Click(object sender, RoutedEventArgs e)
    {
        if (_rec.State is RecorderState.Recording or RecorderState.Paused) { await _rec.StopAsync(); return; }
        if (_rec.IsActive) return;
        if (_saveDebounce.IsEnabled) { _saveDebounce.Stop(); SaveSettings(); }
        var src = CurrentSource();
        if (src == null) { App.MainWindow.ShowBanner("Choose what to record", "Select a display, window or region first.", InfoBarSeverity.Warning); return; }
        await _rec.StartAsync(src);
    }

    private void Pause_Click(object sender, RoutedEventArgs e) => _rec.TogglePause();
    private async void Stop_Click(object sender, RoutedEventArgs e) => await _rec.StopAsync();

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = _settings.Current.RecordingDir;
        try { Directory.CreateDirectory(dir); Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true }); }
        catch (Exception ex) { App.MainWindow.ShowBanner("Cannot open folder", ex.Message, InfoBarSeverity.Error); }
    }

    private void OnState(RecorderState s) => UpdateLive();

    private void UpdateLive()
    {
        var state = _rec.State;
        bool live = state is RecorderState.Recording or RecorderState.Paused;
        TimerText.Text = live || state == RecorderState.Stopping ? UiHelpers.FormatDuration(_rec.Elapsed.TotalSeconds) : "00:00";
        StateText.Text = state switch
        {
            RecorderState.Recording => "REC RUNNING",
            RecorderState.Paused => "PAUSED",
            RecorderState.Starting => "STARTING",
            RecorderState.Stopping => "SAVING",
            RecorderState.Faulted => "LAST RUN FAILED",
            _ => "IDLE",
        };
        var accent = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["PxAccent"];
        StateDot.Fill = state == RecorderState.Recording ? accent : UiHelpers.ThemeBrush("PxTextMuted", ActualTheme);
        StateText.Foreground = state == RecorderState.Recording ? accent : UiHelpers.ThemeBrush("PxTextSecondary", ActualTheme);
        PauseButton.IsEnabled = live;
        StopButton.IsEnabled = live;
        PauseText.Text = state == RecorderState.Paused ? "Resume" : "Pause";
        PauseGlyph.Glyph = state == RecorderState.Paused ? "" : "";
        RecButtonText.Text = live ? "STOP" : "REC";
        RecGlyphDot.Visibility = live ? Visibility.Collapsed : Visibility.Visible;
        RecGlyphStop.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        RecCaption.Text = live ? "Stop and save" : "Start capture";
        foreach (var c in new Control[] { SrcScreen, SrcWindow, SrcRegion, MonitorBox, FpsBox, QualityBox, ResBox, CursorBox, MicSwitch, SysSwitch, MicBox, Delay0, Delay3, Delay5 })
            c.IsEnabled = !_rec.IsActive;

        var s = _settings.Current;
        StatusDetail.Text = live
            ? $"Recording to {Path.GetFileName(s.RecordingDir)}\\ · {s.RecordingFrameRate} fps" + (_rec.HasMicrophone ? " · mic" : "") + (_rec.HasSystemAudio ? " · system audio" : "")
            : _rec.LastResult is { } r ? $"Last: {Path.GetFileName(r.Path)} · {r.Width}×{r.Height} · {UiHelpers.FormatDuration(r.Duration.TotalSeconds)}"
            : $"Saves to {s.RecordingDir}";
        MicLevel.Value = live ? Math.Clamp(_rec.MicrophoneLevel, 0, 1) : 0;
        SysLevel.Value = live ? Math.Clamp(_rec.SystemLevel, 0, 1) : 0;
    }
}
