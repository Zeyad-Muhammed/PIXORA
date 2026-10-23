using Microsoft.UI.Dispatching;
using PIXORA.App.Views;
using PIXORA.Capture;
using PIXORA.Core;
using PIXORA.Recording;

namespace PIXORA.App.Services;

/// <summary>UI-thread facade over <see cref="ScreenRecorder"/>: source picking, countdown, HUD, saving to the library.</summary>
public sealed class RecordingController
{
    private readonly SettingsStore _settings;
    private readonly MediaLibraryService _library;
    private readonly Notifier _notify;
    private readonly DispatcherQueue _ui;
    private ScreenRecorder _recorder = new();
    private RecordingHud? _hud;
    private bool _starting;
    private RecordingOptions? _options;

    public RecordingController(SettingsStore settings, MediaLibraryService library, Notifier notify, DispatcherQueue ui)
    {
        _settings = settings; _library = library; _notify = notify; _ui = ui;
        Hook(_recorder);
    }

    public RecorderState State => _recorder.State;
    public bool IsActive => State is RecorderState.Recording or RecorderState.Paused or RecorderState.Stopping || _starting;
    public TimeSpan Elapsed => _recorder.Elapsed;
    public float SystemLevel => _recorder.SystemLevel;
    public float MicrophoneLevel => _recorder.MicrophoneLevel;
    public bool HasSystemAudio => _options?.SystemAudio == true && !_recorder.Warnings.Any(w => w.StartsWith("System audio"));
    public bool HasMicrophone => _options?.Microphone == true && !_recorder.Warnings.Any(w => w.StartsWith("The microphone"));
    public RecordingResult? LastResult { get; private set; }

    public event Action<RecorderState>? StateChanged;
    public event Action<RecordingResult, MediaItem>? Saved;

    private void Hook(ScreenRecorder r)
    {
        r.StateChanged += s => _ui.TryEnqueue(() => { App.Shell?.SetRecording(s is RecorderState.Recording or RecorderState.Paused); StateChanged?.Invoke(s); });
        r.Failed += msg => _ui.TryEnqueue(() =>
        {
            CloseHud();
            _notify.Error("Recording stopped", msg);
            StateChanged?.Invoke(_recorder.State);
            _ = _library.RefreshAsync();
        });
    }

    public void SetSystemVolume(double v) => _recorder.SetSystemVolume((float)v);
    public void SetMicrophoneVolume(double v) => _recorder.SetMicrophoneVolume((float)v);

    /// <summary>Interactively picks a region or window, or uses the given monitor, then starts recording.</summary>
    public async Task<bool> PickAndStartAsync(RecordingSourceKind kind, MonitorInfo? monitor = null)
    {
        if (IsActive) return false;
        var main = App.MainWindow;
        RecordingSource? source = null;
        if (kind == RecordingSourceKind.Monitor)
            source = RecordingSource.ForMonitor(monitor ?? ScreenCapture.GetMonitors().First(m => m.IsPrimary));
        else
        {
            bool wasVisible = main.IsShown;
            if (wasVisible) { main.HideForCapture(); await Task.Delay(250); }
            try
            {
                using var sel = await ScreenSelector.SelectAsync(kind == RecordingSourceKind.Window ? SelectMode.Window : SelectMode.Region,
                    kind == RecordingSourceKind.Window ? "Click the window to record" : "Drag to select the area to record", new[] { WindowChrome.Handle(main) });
                if (sel != null)
                    source = kind == RecordingSourceKind.Window ? RecordingSource.ForWindow(sel.Result.Window) : RecordingSource.ForRegion(sel.Result.Rect);
            }
            finally
            {
                if (source == null && wasVisible) main.ShowAndActivate();
            }
        }
        if (source == null) return false;
        return await StartAsync(source);
    }

    public async Task<bool> StartAsync(RecordingSource source)
    {
        if (IsActive) return false;
        _starting = true;
        StateChanged?.Invoke(State);
        var main = App.MainWindow;
        try
        {
            var s = _settings.Current;
            var monitor = MonitorFor(source);
            if (main.IsShown) main.Minimize();

            if (!await CountdownWindow.RunAsync(s.RecordingCountdownSeconds, "Recording starts in", monitor))
            {
                main.ShowAndActivate();
                return false;
            }

            var problem = SettingsValidator.ProbeWritable(s.RecordingDir);
            if (problem != null) throw new RecordingException(problem);
            var name = NamingTemplate.Expand(s.RecordingNameTemplate, DateTime.Now);
            var output = NamingTemplate.UniquePath(s.RecordingDir, name, ".mp4");
            _options = new RecordingOptions
            {
                Source = source, OutputPath = output, FrameRate = s.RecordingFrameRate, OutputHeight = s.RecordingHeight,
                Quality = s.RecordingQuality, CaptureCursor = s.CaptureCursor,
                SystemAudio = s.RecordSystemAudio, Microphone = s.RecordMicrophone, MicrophoneDeviceId = s.MicrophoneDeviceId,
                SystemVolume = (float)s.SystemAudioVolume, MicrophoneVolume = (float)s.MicrophoneVolume,
            };
            if (_recorder.State == RecorderState.Faulted) { _recorder = new ScreenRecorder(); Hook(_recorder); }
            await _recorder.StartAsync(_options);
            foreach (var w in _recorder.Warnings) _notify.Warning("Recording", w);

            _hud = new RecordingHud(this, monitor);
            _hud.Activate();
            return true;
        }
        catch (RecordingException ex)
        {
            _notify.Error("Recording could not start", ex.Message);
            main.ShowAndActivate();
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("Recording start failed", ex);
            _notify.Error("Recording could not start", ex.Message);
            main.ShowAndActivate();
            return false;
        }
        finally
        {
            _starting = false;
            StateChanged?.Invoke(State);
        }
    }

    private static MonitorInfo MonitorFor(RecordingSource source)
    {
        var mons = ScreenCapture.GetMonitors();
        return source.Kind switch
        {
            RecordingSourceKind.Monitor => mons.FirstOrDefault(m => m.Handle == source.Monitor) ?? mons.First(m => m.IsPrimary),
            RecordingSourceKind.Region => ScreenCapture.MonitorAt((source.Region.Left + source.Region.Right) / 2, (source.Region.Top + source.Region.Bottom) / 2) ?? mons.First(m => m.IsPrimary),
            _ => mons.First(m => m.IsPrimary),
        };
    }

    public void TogglePause()
    {
        if (_recorder.State == RecorderState.Recording) _recorder.Pause();
        else if (_recorder.State == RecorderState.Paused) _recorder.Resume();
    }

    public async Task StopAsync()
    {
        if (_recorder.State is not (RecorderState.Recording or RecorderState.Paused)) return;
        try
        {
            var result = await _recorder.StopAsync();
            LastResult = result;
            CloseHud();
            var item = await _library.AddAsync(result.Path, MediaKind.Recording);
            var detail = $"{Path.GetFileName(result.Path)} · {UiHelpers.FormatDuration(result.Duration.TotalSeconds)}";
            if (result.FramesDropped > 0) detail += $" · {result.FramesDropped} frames dropped";
            _notify.Info("Recording saved", detail);
            Saved?.Invoke(result, item);
            var main = App.MainWindow;
            main.ShowAndActivate();
            main.Navigate(PageKind.Preview, item.Id);
        }
        catch (RecordingException ex)
        {
            CloseHud();
            _notify.Error("Recording failed", ex.Message);
            App.MainWindow.ShowAndActivate();
        }
    }

    /// <summary>Hotkey/tray toggle: stop if recording, otherwise record the primary display.</summary>
    public async Task ToggleAsync()
    {
        if (State is RecorderState.Recording or RecorderState.Paused) await StopAsync();
        else if (!IsActive) await PickAndStartAsync(RecordingSourceKind.Monitor);
    }

    private void CloseHud()
    {
        _hud?.Close();
        _hud = null;
    }

    /// <summary>Called on app exit: finalises an in-progress recording so the file is playable.</summary>
    public async Task ShutdownAsync()
    {
        if (_recorder.State is RecorderState.Recording or RecorderState.Paused)
        {
            try { var r = await _recorder.StopAsync(); await _library.AddAsync(r.Path, MediaKind.Recording); } catch (Exception ex) { Log.Error("Stop on exit failed", ex); }
        }
        CloseHud();
    }
}
