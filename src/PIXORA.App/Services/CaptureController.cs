using PIXORA.App.Views;
using PIXORA.Capture;
using PIXORA.Core;
using SkiaSharp;

namespace PIXORA.App.Services;

public sealed record CaptureOutcome(string? SavedPath, int Width, int Height, bool CopiedToClipboard);

/// <summary>Runs the screenshot workflow: hide UI, optional delay, capture, save, clipboard, open in editor.</summary>
public sealed class CaptureController
{
    private readonly SettingsStore _settings;
    private readonly MediaLibraryService _library;
    private readonly EditorSession _editor;
    private readonly Notifier _notify;
    private bool _busy;

    public CaptureController(SettingsStore settings, MediaLibraryService library, EditorSession editor, Notifier notify)
    {
        _settings = settings; _library = library; _editor = editor; _notify = notify;
    }

    public CaptureOutcome? LastOutcome { get; private set; }
    public event Action<CaptureOutcome>? Captured;

    /// <param name="monitor">Only used for <see cref="CaptureMode.Monitor"/>.</param>
    public async Task<CaptureOutcome?> CaptureAsync(CaptureMode mode, MonitorInfo? monitor = null, int? delaySeconds = null)
    {
        if (_busy) return null;
        _busy = true;
        var main = App.MainWindow;
        bool wasVisible = main.IsShown;
        SKBitmap? bitmap = null;
        try
        {
            if (wasVisible) { main.HideForCapture(); await Task.Delay(250); }

            int delay = delaySeconds ?? _settings.Current.CaptureDelaySeconds;
            if (delay > 0 && !await CountdownWindow.RunAsync(delay, "Capturing in"))
                return null;

            bitmap = mode switch
            {
                CaptureMode.FullScreen => ScreenCapture.CaptureVirtualScreen(),
                CaptureMode.Monitor => ScreenCapture.CaptureMonitor(monitor ?? ScreenCapture.GetMonitors().First(m => m.IsPrimary)),
                CaptureMode.Region => await SelectRegionAsync(),
                CaptureMode.Window => await SelectWindowAsync(main),
                _ => null,
            };
            if (bitmap == null) return null; // cancelled

            return await DeliverAsync(bitmap);
        }
        catch (CaptureException ex)
        {
            Log.Warn($"Capture failed: {ex.Message}");
            _notify.Error("Capture failed", ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("Capture failed", ex);
            _notify.Error("Capture failed", "Something went wrong while capturing. Details were written to the log.");
            return null;
        }
        finally
        {
            bitmap?.Dispose();
            _busy = false;
            if (wasVisible && !main.IsShown) main.ShowAndActivate();
        }
    }

    private static async Task<SKBitmap?> SelectRegionAsync()
    {
        using var sel = await ScreenSelector.SelectAsync(SelectMode.Region, "Drag to select an area, or click a window", new[] { WindowChrome.Handle(App.MainWindow) });
        return sel?.CropRegion();
    }

    private static async Task<SKBitmap?> SelectWindowAsync(MainWindow main)
    {
        using var sel = await ScreenSelector.SelectAsync(SelectMode.Window, "Click the window to capture", new[] { WindowChrome.Handle(main) });
        if (sel == null) return null;
        return ScreenCapture.CaptureWindow(sel.Result.Window);
    }

    private async Task<CaptureOutcome> DeliverAsync(SKBitmap bitmap)
    {
        var s = _settings.Current;
        string? path = null;
        bool copied = false;

        if (s.AutoSaveScreenshots)
        {
            path = await Task.Run(() => ScreenshotSaver.Save(bitmap, s));
            await _library.AddAsync(path, MediaKind.Screenshot);
        }
        if (s.CopyToClipboardAfterCapture)
        {
            try { ClipboardService.SetImage(bitmap); copied = true; }
            catch (CaptureException ex) { _notify.Error("Clipboard", ex.Message); }
        }

        var outcome = new CaptureOutcome(path, bitmap.Width, bitmap.Height, copied);
        LastOutcome = outcome;
        Captured?.Invoke(outcome);

        if (s.OpenEditorAfterCapture)
        {
            var main = App.MainWindow;
            if (await main.ConfirmDiscardEditorChangesAsync())
            {
                _editor.OpenBitmap(bitmap.Copy(), path);
                main.ShowAndActivate();
                main.Navigate(PageKind.Editor);
            }
        }

        var detail = path != null ? $"Saved {Path.GetFileName(path)}" : "Not saved (auto-save is off)";
        if (copied) detail += " · copied to clipboard";
        _notify.Info($"Screenshot captured ({bitmap.Width}×{bitmap.Height})", detail);
        return outcome;
    }
}
