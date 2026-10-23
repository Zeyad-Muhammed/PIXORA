using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using PIXORA.App.Services;
using PIXORA.App.ViewModels;
using PIXORA.Capture;
using PIXORA.Core;
using PIXORA.Library;

namespace PIXORA.App;

public partial class App : Application
{
    private const string MutexName = @"Local\NOVU.PIXORA.CaptureStudio";
    private const string ActivateEventName = @"Local\NOVU.PIXORA.CaptureStudio.Activate";

    private Mutex? _mutex;
    private EventWaitHandle? _activateEvent;
    private bool _quitting;

    public static MainWindow MainWindow { get; private set; } = null!;
    public static ShellHost? Shell { get; private set; }
    public static IServiceProvider Services { get; private set; } = null!;
    public static T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    public static string Version =>
        typeof(App).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            Log.Error("Unhandled UI exception", e.Exception);
            e.Handled = true;
            MainWindow?.ShowBanner("Unexpected error", e.Exception.Message, Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Fatal exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("Unobserved task exception", e.Exception); e.SetObserved(); };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            // Another instance is running (e.g. launched again from NOVU Launcher): bring it to front and leave.
            try { EventWaitHandle.OpenExisting(ActivateEventName).Set(); } catch (WaitHandleCannotBeOpenedException) { }
            Exit();
            return;
        }

        ScreenCapture.EnsureDpiAwareness();
        var paths = AppPaths.Default();
        paths.EnsureCreated();
        Log.Init(paths);
        paths.CleanTemp(TimeSpan.FromDays(1));
        Log.Info($"PIXORA {Version} starting");

        var settings = new SettingsStore(paths);
        settings.Load();
        CleanAbandonedRecordings(settings.Current.RecordingDir);
        var ui = DispatcherQueue.GetForCurrentThread();

        var sc = new ServiceCollection();
        sc.AddSingleton(paths);
        sc.AddSingleton(settings);
        sc.AddSingleton(ui);
        sc.AddSingleton(_ => new LibraryStore(paths.LibraryDb));
        sc.AddSingleton(_ => new ThumbnailService(paths.ThumbDir));
        sc.AddSingleton<MediaLibraryService>();
        sc.AddSingleton<EditorSession>();
        sc.AddSingleton<Notifier>();
        sc.AddSingleton<CaptureController>();
        sc.AddSingleton<RecordingController>();
        sc.AddTransient<HomeViewModel>();
        sc.AddTransient<LibraryViewModel>();
        sc.AddTransient<SettingsViewModel>();
        Services = sc.BuildServiceProvider();

        MainWindow = new MainWindow();

        Shell = new ShellHost(ui);
        Shell.HotkeyPressed += OnHotkey;
        Shell.TrayInvoked += OnTray;
        ApplyShellSettings(settings.Current);
        settings.Changed += s => ui.TryEnqueue(() => ApplyShellSettings(s));

        bool startHidden = Environment.GetCommandLineArgs().Contains("--tray") && settings.Current.ShowTrayIcon;
        if (!startHidden) MainWindow.Activate();

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        var listener = new Thread(() =>
        {
            while (!_quitting && _activateEvent.WaitOne())
                if (!_quitting) ui.TryEnqueue(() => MainWindow.ShowAndActivate());
        }) { IsBackground = true, Name = "PIXORA activation listener" };
        listener.Start();

        _ = Get<MediaLibraryService>().RefreshAsync();
    }

    /// <summary>
    /// A recording interrupted by a crash or power loss leaves an unplayable "*.mp4.part" (the MP4 index is written
    /// only on stop). Remove stale ones so they do not accumulate; completed recordings are never touched.
    /// </summary>
    private static void CleanAbandonedRecordings(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, "*.mp4.part"))
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) < TimeSpan.FromMinutes(10)) continue;
                File.Delete(f);
                Log.Warn($"Removed incomplete recording left by an earlier session: {Path.GetFileName(f)}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn($"Cleanup skipped: {ex.Message}"); }
    }

    private void ApplyShellSettings(AppSettings s)
    {
        Shell?.ApplyHotkeys(s.Hotkeys);
        Shell?.SetTrayVisible(s.ShowTrayIcon);
        StartupRegistration.Apply(s.StartWithWindows);
        MainWindow?.ApplyTheme(s.Theme);
        MainWindow?.RefreshShortcutHints();
    }

    private async void OnHotkey(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.RegionCapture: await Get<CaptureController>().CaptureAsync(CaptureMode.Region); break;
            case HotkeyAction.FullScreenCapture: await Get<CaptureController>().CaptureAsync(CaptureMode.FullScreen); break;
            case HotkeyAction.WindowCapture: await Get<CaptureController>().CaptureAsync(CaptureMode.Window); break;
            case HotkeyAction.ToggleRecording: await Get<RecordingController>().ToggleAsync(); break;
            case HotkeyAction.OpenEditor: MainWindow.ShowAndActivate(); MainWindow.Navigate(PageKind.Editor); break;
        }
    }

    private async void OnTray(TrayCommand cmd)
    {
        switch (cmd)
        {
            case TrayCommand.Open: MainWindow.ShowAndActivate(); break;
            case TrayCommand.CaptureRegion: await Get<CaptureController>().CaptureAsync(CaptureMode.Region); break;
            case TrayCommand.CaptureWindow: await Get<CaptureController>().CaptureAsync(CaptureMode.Window); break;
            case TrayCommand.CaptureFullScreen: await Get<CaptureController>().CaptureAsync(CaptureMode.FullScreen); break;
            case TrayCommand.ToggleRecording: await Get<RecordingController>().ToggleAsync(); break;
            case TrayCommand.OpenLibrary: MainWindow.ShowAndActivate(); MainWindow.Navigate(PageKind.Library); break;
            case TrayCommand.Settings: MainWindow.ShowAndActivate(); MainWindow.Navigate(PageKind.Settings); break;
            case TrayCommand.Quit: await QuitAsync(); break;
        }
    }

    /// <summary>Full shutdown: finalises any recording, releases the tray/hotkeys and exits.</summary>
    public async Task QuitAsync()
    {
        if (_quitting) return;
        if (!await MainWindow.ConfirmDiscardEditorChangesAsync()) return;
        _quitting = true;
        Log.Info("PIXORA shutting down");
        await Get<RecordingController>().ShutdownAsync();
        Shell?.Dispose();
        _activateEvent?.Set();
        MainWindow.CloseForExit();
        _mutex?.ReleaseMutex();
        Exit();
    }

    public bool IsQuitting => _quitting;
}
