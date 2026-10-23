using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PIXORA.App.Pages;
using PIXORA.App.Services;
using PIXORA.Core;
using PIXORA.Recording;
using Windows.Graphics;
using Windows.System;

namespace PIXORA.App;

public enum PageKind { Home, Screenshot, Record, Editor, Library, Preview, Settings }

public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);

    private readonly IntPtr _hwnd;
    private readonly SettingsStore _settings;
    private readonly MediaLibraryService _library;
    private readonly EditorSession _editor;
    private readonly RecordingController _recording;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _bannerTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private PageKind _current = PageKind.Home;
    private bool _closingForExit;

    public MainWindow()
    {
        InitializeComponent();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _settings = App.Get<SettingsStore>();
        _library = App.Get<MediaLibraryService>();
        _editor = App.Get<EditorSession>();
        _recording = App.Get<RecordingController>();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        var tb = AppWindow.TitleBar;
        tb.ButtonBackgroundColor = Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Colors.Transparent;
        tb.PreferredHeightOption = TitleBarHeightOption.Tall;
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "pixora.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);

        var scale = Interop.Win32.GetDpiForWindow(_hwnd) / 96.0;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int w = Math.Min((int)(1320 * scale), area.Width - 40), h = Math.Min((int)(880 * scale), area.Height - 40);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
        AppWindow.Closing += OnClosing;
        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated && Root.XamlRoot != null && FocusManager.GetFocusedElement(Root.XamlRoot) == null)
                FocusContent();
        };

        VersionText.Text = "V" + App.Version;
        ApplyTheme(_settings.Current.Theme);
        RefreshShortcutHints();
        AddAccelerators();

        _library.Changed += () => _ = UpdateLibraryCountAsync();
        _recording.StateChanged += _ => UpdateRecordingIndicator();
        _statusTimer.Tick += (_, _) => { UnsavedDot.Visibility = _editor.HasUnsavedChanges ? Visibility.Visible : Visibility.Collapsed; };
        _statusTimer.Start();
        _bannerTimer.Tick += (_, _) => { _bannerTimer.Stop(); Banner.IsOpen = false; };

        Navigate(PageKind.Home);
        _ = UpdateLibraryCountAsync();
        UpdateStorage();
        UpdateRecordingIndicator();
    }

    // ---- window state --------------------------------------------------------------------------------------

    public bool IsShown => AppWindow.IsVisible && !(AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized });
    public bool IsForeground => GetForegroundWindow() == _hwnd;

    public void HideForCapture() => AppWindow.Hide();

    public void Minimize()
    {
        if (AppWindow.Presenter is OverlappedPresenter p) p.Minimize();
    }

    public void ShowAndActivate()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) p.Restore();
        Activate();
        SetForegroundWindow(_hwnd);
    }

    public void CloseForExit()
    {
        _closingForExit = true;
        _statusTimer.Stop();
        Close();
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closingForExit) return;
        args.Cancel = true;
        if (_settings.Current.MinimizeToTray && App.Shell?.TrayVisible == true)
        {
            AppWindow.Hide();
            return;
        }
        if (_recording.IsActive)
        {
            var r = await UiHelpers.ShowDialogAsync(Root.XamlRoot, "Recording in progress", "Quitting will stop and save the current recording.", "Stop and quit");
            if (r != ContentDialogResult.Primary) return;
        }
        await ((App)Application.Current).QuitAsync();
    }

    public void ApplyTheme(AppTheme theme)
    {
        Root.RequestedTheme = theme switch { AppTheme.Light => ElementTheme.Light, AppTheme.System => ElementTheme.Default, _ => ElementTheme.Dark };
        var dark = Root.ActualTheme != ElementTheme.Light;
        var tb = AppWindow.TitleBar;
        tb.ButtonForegroundColor = dark ? Windows.UI.Color.FromArgb(255, 0xE5, 0xEE, 0xF0) : Windows.UI.Color.FromArgb(255, 0x17, 0x17, 0x1A);
        tb.ButtonHoverBackgroundColor = dark ? Windows.UI.Color.FromArgb(255, 0x22, 0x22, 0x26) : Windows.UI.Color.FromArgb(255, 0xE6, 0xE6, 0xEA);
        tb.ButtonHoverForegroundColor = tb.ButtonForegroundColor;
        tb.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(255, 0x77, 0x77, 0x80);
        UpdateNavSelection();
    }

    public void RefreshShortcutHints()
    {
        var hk = _settings.Current.Hotkeys;
        ScreenshotHint.Text = hk.RegionCapture;
        RecordHint.Text = _recording.IsActive ? "" : hk.ToggleRecording;
        if (ContentFrame.Content is IRefreshable r) r.Refresh();
    }

    // ---- navigation ----------------------------------------------------------------------------------------

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<PageKind>(tag, out var kind)) Navigate(kind);
    }

    public void Navigate(PageKind kind, object? parameter = null)
    {
        var type = kind switch
        {
            PageKind.Screenshot => typeof(ScreenshotPage),
            PageKind.Record => typeof(RecordPage),
            PageKind.Editor => typeof(EditorPage),
            PageKind.Library => typeof(LibraryPage),
            PageKind.Preview => typeof(PreviewPage),
            PageKind.Settings => typeof(SettingsPage),
            _ => typeof(HomePage),
        };
        _current = kind;
        try
        {
            ContentFrame.Navigate(type, parameter);
        }
        catch (Exception ex)
        {
            Log.Error($"Navigation to {kind} failed", ex);
            ShowBanner("Page could not open", ex.Message, InfoBarSeverity.Error);
            return;
        }
        FocusContent();
        UpdateNavSelection();
    }

    private void UpdateNavSelection()
    {
        var navKind = _current == PageKind.Preview ? PageKind.Library : _current;
        var theme = Root.ActualTheme;
        foreach (var b in new[] { NavHome, NavScreenshot, NavRecord, NavEditor, NavLibrary, NavSettings })
        {
            bool selected = (string)b.Tag == navKind.ToString();
            b.Background = selected ? UiHelpers.ThemeBrush("PxAccentWash", theme) : new SolidColorBrush(Colors.Transparent);
            b.BorderBrush = selected ? (Brush)Application.Current.Resources["PxAccent"] : new SolidColorBrush(Colors.Transparent);
            b.Foreground = UiHelpers.ThemeBrush(selected ? "PxText" : "PxTextSecondary", theme);
            b.FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(b, selected ? "Selected" : "");
        }
    }

    public PageKind CurrentPage => _current;

    /// <summary>Gives the page keyboard focus so shortcuts work right after launch or navigation (no visible focus ring).</summary>
    private void FocusContent()
    {
        if (ContentFrame.Content is not Control page) return;
        page.IsTabStop = true;
        page.UseSystemFocusVisuals = false;
        if (page.IsLoaded) page.Focus(FocusState.Programmatic);
        else
        {
            void OnLoaded(object s, RoutedEventArgs e) { page.Loaded -= OnLoaded; page.Focus(FocusState.Programmatic); }
            page.Loaded += OnLoaded;
        }
    }

    private void AddAccelerators()
    {
        void Add(VirtualKey key, PageKind kind)
        {
            var a = new KeyboardAccelerator { Key = key, Modifiers = VirtualKeyModifiers.Control };
            a.Invoked += (_, e) => { Navigate(kind); e.Handled = true; };
            Root.KeyboardAccelerators.Add(a);
        }
        Add(VirtualKey.Number1, PageKind.Home);
        Add(VirtualKey.Number2, PageKind.Screenshot);
        Add(VirtualKey.Number3, PageKind.Record);
        Add(VirtualKey.Number4, PageKind.Editor);
        Add(VirtualKey.Number5, PageKind.Library);
        var settings = new KeyboardAccelerator { Key = (VirtualKey)0xBC, Modifiers = VirtualKeyModifiers.Control }; // Ctrl+,
        settings.Invoked += (_, e) => { Navigate(PageKind.Settings); e.Handled = true; };
        Root.KeyboardAccelerators.Add(settings);
        Root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
    }

    // ---- status --------------------------------------------------------------------------------------------

    private async Task UpdateLibraryCountAsync()
    {
        var n = await _library.CountAsync(LibraryCategory.All);
        LibraryCount.Text = n.ToString();
        UpdateStorage();
    }

    private void UpdateRecordingIndicator()
    {
        bool rec = _recording.State is RecorderState.Recording or RecorderState.Paused;
        RecordingDot.Visibility = rec ? Visibility.Visible : Visibility.Collapsed;
        RecordHint.Visibility = rec ? Visibility.Collapsed : Visibility.Visible;
    }

    public void UpdateStorage()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(_settings.Current.ScreenshotDir));
            var d = new DriveInfo(root!);
            StorageText.Text = $"Storage: {UiHelpers.FormatSize(d.AvailableFreeSpace)} free";
            StorageBar.Value = 100.0 * (d.TotalSize - d.AvailableFreeSpace) / d.TotalSize;
            ToolTipService.SetToolTip(StorageText, $"Drive {d.Name} · {UiHelpers.FormatSize(d.TotalSize)} total");
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            StorageText.Text = "Storage: unavailable";
            StorageBar.Value = 0;
        }
    }

    public void ShowBanner(string title, string message, InfoBarSeverity severity)
    {
        Banner.Title = title;
        Banner.Message = message;
        Banner.Severity = severity;
        Banner.IsOpen = true;
        _bannerTimer.Stop();
        _bannerTimer.Interval = severity == InfoBarSeverity.Error ? TimeSpan.FromSeconds(12) : TimeSpan.FromSeconds(6);
        _bannerTimer.Start();
    }

    /// <summary>Asks to save the editor document if it has unsaved changes. Returns false when the user cancels.</summary>
    public async Task<bool> ConfirmDiscardEditorChangesAsync()
    {
        if (!_editor.HasUnsavedChanges) return true;
        if (!IsShown) ShowAndActivate();
        var r = await UiHelpers.ShowDialogAsync(Root.XamlRoot, "Unsaved changes",
            "The image in the editor has changes that are not saved. Save them first?", "Save", "Don't save");
        if (r == ContentDialogResult.None) return false;
        if (r == ContentDialogResult.Primary) return await _editor.SaveAsync(false) != null;
        return true;
    }
}

/// <summary>Pages that re-read settings when they change (e.g. shortcut labels).</summary>
public interface IRefreshable
{
    void Refresh();
}
