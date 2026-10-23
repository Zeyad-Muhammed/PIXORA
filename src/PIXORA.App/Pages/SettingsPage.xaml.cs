using System.Diagnostics;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using PIXORA.App.Services;
using PIXORA.App.ViewModels;
using PIXORA.Core;
using PIXORA.Recording;
using Windows.System;
using Windows.UI.Core;

namespace PIXORA.App.Pages;

public sealed partial class SettingsPage : Page
{
    private readonly SettingsViewModel _vm = App.Get<SettingsViewModel>();
    private IReadOnlyList<AudioDeviceInfo> _mics = Array.Empty<AudioDeviceInfo>();

    public SettingsPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        VersionText.Text = $"Version {App.Version} · Windows App SDK · .NET {Environment.Version.Major}";
        _mics = await Task.Run(AudioDevices.GetMicrophones);
        MicBox.ItemsSource = _mics;
        _vm.Reload();
        Load(_vm.Draft);
    }

    private static void Select(ComboBox box, string tag) =>
        box.SelectedItem = box.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag) ?? box.Items[0];

    private static string Tag(ComboBox box) => (string)((ComboBoxItem)box.SelectedItem).Tag;

    private void Load(AppSettings s)
    {
        StartupSwitch.IsOn = s.StartWithWindows;
        TraySwitch.IsOn = s.ShowTrayIcon;
        MinimizeSwitch.IsOn = s.MinimizeToTray;
        MinimizeSwitch.IsEnabled = s.ShowTrayIcon;
        NotifySwitch.IsOn = s.ShowNotifications;
        Select(ThemeBox, s.Theme.ToString());
        ShotDirBox.Text = s.ScreenshotDir;
        RecDirBox.Text = s.RecordingDir;
        ShotNameBox.Text = s.ScreenshotNameTemplate;
        RecNameBox.Text = s.RecordingNameTemplate;
        Select(FormatBox, s.ScreenshotFormat.ToString());
        QualitySlider.Value = s.ImageQuality;
        DelayBox.Value = s.CaptureDelaySeconds;
        AutoSaveSwitch.IsOn = s.AutoSaveScreenshots;
        ClipSwitch.IsOn = s.CopyToClipboardAfterCapture;
        EditorSwitch.IsOn = s.OpenEditorAfterCapture;
        Select(FpsBox, s.RecordingFrameRate.ToString());
        Select(ResBox, s.RecordingHeight.ToString());
        Select(QualityBox, s.RecordingQuality.ToString());
        CountdownBox.Value = s.RecordingCountdownSeconds;
        CursorSwitch.IsOn = s.CaptureCursor;
        SysAudioSwitch.IsOn = s.RecordSystemAudio;
        SysVolSlider.Value = s.SystemAudioVolume * 100;
        MicSwitch.IsOn = s.RecordMicrophone;
        MicBox.SelectedItem = _mics.FirstOrDefault(m => m.Id == s.MicrophoneDeviceId) ?? _mics.FirstOrDefault(m => m.IsDefault) ?? _mics.FirstOrDefault();
        MicVolSlider.Value = s.MicrophoneVolume * 100;
        HkRegion.Text = s.Hotkeys.RegionCapture;
        HkWindow.Text = s.Hotkeys.WindowCapture;
        HkFull.Text = s.Hotkeys.FullScreenCapture;
        HkRecord.Text = s.Hotkeys.ToggleRecording;
        HkEditor.Text = s.Hotkeys.OpenEditor;
        ErrorBar.IsOpen = false;
        SaveStatus.Text = "";
        UpdatePreviews();
        UpdateVolLabels();
    }

    private void Collect(AppSettings s)
    {
        s.StartWithWindows = StartupSwitch.IsOn;
        s.ShowTrayIcon = TraySwitch.IsOn;
        s.MinimizeToTray = MinimizeSwitch.IsOn && TraySwitch.IsOn;
        s.ShowNotifications = NotifySwitch.IsOn;
        s.Theme = Enum.Parse<AppTheme>(Tag(ThemeBox));
        s.ScreenshotDir = ShotDirBox.Text.Trim();
        s.RecordingDir = RecDirBox.Text.Trim();
        s.ScreenshotNameTemplate = ShotNameBox.Text.Trim();
        s.RecordingNameTemplate = RecNameBox.Text.Trim();
        s.ScreenshotFormat = Enum.Parse<ImageFormat>(Tag(FormatBox));
        s.ImageQuality = (int)QualitySlider.Value;
        s.CaptureDelaySeconds = double.IsNaN(DelayBox.Value) ? 0 : (int)DelayBox.Value;
        s.AutoSaveScreenshots = AutoSaveSwitch.IsOn;
        s.CopyToClipboardAfterCapture = ClipSwitch.IsOn;
        s.OpenEditorAfterCapture = EditorSwitch.IsOn || (!AutoSaveSwitch.IsOn && !ClipSwitch.IsOn);
        s.RecordingFrameRate = int.Parse(Tag(FpsBox));
        s.RecordingHeight = int.Parse(Tag(ResBox));
        s.RecordingQuality = Enum.Parse<VideoQuality>(Tag(QualityBox));
        s.RecordingCountdownSeconds = double.IsNaN(CountdownBox.Value) ? 0 : (int)CountdownBox.Value;
        s.CaptureCursor = CursorSwitch.IsOn;
        s.RecordSystemAudio = SysAudioSwitch.IsOn;
        s.SystemAudioVolume = SysVolSlider.Value / 100;
        s.RecordMicrophone = MicSwitch.IsOn;
        s.MicrophoneDeviceId = (MicBox.SelectedItem as AudioDeviceInfo)?.Id;
        s.MicrophoneVolume = MicVolSlider.Value / 100;
        s.Hotkeys.RegionCapture = HkRegion.Text;
        s.Hotkeys.WindowCapture = HkWindow.Text;
        s.Hotkeys.FullScreenCapture = HkFull.Text;
        s.Hotkeys.ToggleRecording = HkRecord.Text;
        s.Hotkeys.OpenEditor = HkEditor.Text;
    }

    private void Tray_Toggled(object sender, RoutedEventArgs e) => MinimizeSwitch.IsEnabled = TraySwitch.IsOn;

    private void Template_Changed(object sender, TextChangedEventArgs e) => UpdatePreviews();

    private void UpdatePreviews()
    {
        var ext = FormatBox.SelectedItem is ComboBoxItem { Tag: string f } ? NamingTemplate.Extension(Enum.Parse<ImageFormat>(f)) : ".png";
        ShotNamePreview.Text = "Example: " + SettingsViewModel.PreviewName(ShotNameBox.Text, ext);
        RecNamePreview.Text = "Example: " + SettingsViewModel.PreviewName(RecNameBox.Text, ".mp4");
    }

    private void Vol_Changed(object sender, RangeBaseValueChangedEventArgs e) => UpdateVolLabels();

    private void UpdateVolLabels()
    {
        SysVolLabel.Text = $"System audio volume: {SysVolSlider.Value:0}%";
        MicVolLabel.Text = $"Microphone volume: {MicVolSlider.Value:0}%";
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var folder = await UiHelpers.PickFolderAsync();
        if (folder == null) return;
        if ((string)((Button)sender).Tag == "Shot") ShotDirBox.Text = folder; else RecDirBox.Text = folder;
    }

    private void Hotkey_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var box = (TextBox)sender;
        e.Handled = true;
        if (e.Key is VirtualKey.Tab) { e.Handled = false; return; }
        if (e.Key is VirtualKey.Back or VirtualKey.Delete) { box.Text = ""; return; }
        if (e.Key is VirtualKey.Control or VirtualKey.Shift or VirtualKey.Menu or VirtualKey.LeftWindows or VirtualKey.RightWindows
            or VirtualKey.LeftControl or VirtualKey.RightControl or VirtualKey.LeftShift or VirtualKey.RightShift or VirtualKey.LeftMenu or VirtualKey.RightMenu) return;
        bool Down(VirtualKey k) => InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(CoreVirtualKeyStates.Down);
        var parts = new List<string>();
        if (Down(VirtualKey.Control)) parts.Add("Ctrl");
        if (Down(VirtualKey.Menu)) parts.Add("Alt");
        if (Down(VirtualKey.Shift)) parts.Add("Shift");
        if (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows)) parts.Add("Win");
        string? key = e.Key switch
        {
            >= VirtualKey.A and <= VirtualKey.Z => e.Key.ToString(),
            >= VirtualKey.Number0 and <= VirtualKey.Number9 => ((int)e.Key - (int)VirtualKey.Number0).ToString(),
            >= VirtualKey.F1 and <= VirtualKey.F24 => e.Key.ToString(),
            VirtualKey.Snapshot => "PrintScreen",
            VirtualKey.Space => "Space",
            _ => null,
        };
        if (key == null || parts.Count == 0) { SaveStatus.Text = "Use at least one modifier (Ctrl, Alt, Shift or Win) with a letter, number, F-key, Space or Print Screen."; return; }
        parts.Add(key);
        box.Text = string.Join("+", parts);
        SaveStatus.Text = "";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Collect(_vm.Draft);
        if (_vm.Save())
        {
            ErrorBar.IsOpen = false;
            SaveStatus.Text = $"Saved at {DateTime.Now:HH:mm:ss}";
            App.MainWindow.UpdateStorage();
            _ = App.Get<MediaLibraryService>().RefreshAsync();
        }
        else
        {
            ErrorBar.Message = _vm.Errors;
            ErrorBar.IsOpen = true;
            SaveStatus.Text = "Not saved";
        }
    }

    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        _vm.Reload();
        Load(_vm.Draft);
    }

    private async void Defaults_Click(object sender, RoutedEventArgs e)
    {
        var r = await UiHelpers.ShowDialogAsync(XamlRoot, "Restore default settings?", "All settings, including folders and shortcuts, return to their defaults. Your files are not affected.", "Restore defaults");
        if (r != ContentDialogResult.Primary) return;
        _vm.RestoreDefaults();
        Load(_vm.Draft);
        SaveStatus.Text = "Defaults restored";
    }

    private void OpenData_Click(object sender, RoutedEventArgs e) => OpenFolder(App.Get<AppPaths>().DataDir);
    private void OpenLogs_Click(object sender, RoutedEventArgs e) => OpenFolder(App.Get<AppPaths>().LogDir);

    private static void OpenFolder(string dir)
    {
        try { Directory.CreateDirectory(dir); Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true }); }
        catch (Exception ex) { App.MainWindow.ShowBanner("Cannot open folder", ex.Message, InfoBarSeverity.Error); }
    }
}
