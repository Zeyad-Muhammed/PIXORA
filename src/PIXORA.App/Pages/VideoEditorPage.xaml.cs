using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using PIXORA.App.Services;
using PIXORA.Core;
using PIXORA.Editor.Video;
using PIXORA.Recording;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;
using Path = System.IO.Path;

namespace PIXORA.App.Pages;

/// <summary>
/// Basic video editing for recordings: timeline with thumbnails, split, delete parts, trim by dragging edges,
/// mute, undo/redo and export to a new MP4. Edits are a cut list over the untouched original.
/// </summary>
public sealed partial class VideoEditorPage : Page, IGuardedPage
{
    private enum Drag { None, Scrub, TrimStart, TrimEnd }

    private const double Pad = 14;          // timeline side padding (DIP)
    private const double RulerHeight = 22;
    private const double TrackTop = 28, TrackHeight = 74;
    private const double HandleHit = 9;
    private const int ThumbCount = 24;

    private readonly MediaLibraryService _library = App.Get<MediaLibraryService>();
    private readonly SettingsStore _settings = App.Get<SettingsStore>();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private MediaItem? _item;
    private VideoTimeline? _tl;
    private MediaPlayer? _player;
    private readonly List<(TimeSpan Time, BitmapImage Image)> _thumbs = new();
    private int _selected = -1;
    private Drag _drag;
    private int _dragIndex;
    private bool _dragRecorded, _wasPlayingBeforeScrub;
    private bool _exported = true;            // nothing to lose until the first edit
    private CancellationTokenSource? _exportCts;
    private Line? _playhead;
    private Polygon? _playheadCap;

    public VideoEditorPage()
    {
        InitializeComponent();
        _tick.Tick += (_, _) => OnTick();
        AddAccelerators();
        KeyDown += Page_KeyDown;
    }

    private double Seconds => _tl?.SourceDuration.TotalSeconds ?? 1;
    private double PxPerSec => Math.Max(1, (TimelineScroller.ActualWidth - 2 * Pad - 4) / Seconds * ZoomSlider.Value);
    private double X(TimeSpan t) => Pad + t.TotalSeconds * PxPerSec;
    private TimeSpan T(double x) => TimeSpan.FromSeconds(Math.Clamp((x - Pad) / PxPerSec, 0, Seconds));

    // ---- lifecycle -----------------------------------------------------------------------------------------

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is long id) _item = _library.Get(id);
        if (_item == null || !File.Exists(_item.Path))
        {
            App.MainWindow.ShowBanner("Cannot edit", "The recording file was not found.", InfoBarSeverity.Error);
            return;
        }
        Title.Text = $"Edit video — {_item.Name}";
        try
        {
            var duration = await VideoEditing.GetDurationAsync(_item.Path);
            _tl = new VideoTimeline(duration);
            _tl.Changed += OnTimelineChanged;
            _player = new MediaPlayer { Source = MediaSource.CreateFromUri(new Uri(_item.Path)), AutoPlay = false };
            _player.MediaFailed += (_, a) => DispatcherQueue.TryEnqueue(() => App.MainWindow.ShowBanner("Playback failed", a.ErrorMessage, InfoBarSeverity.Error));
            Player.SetMediaPlayer(_player);
            Loading.IsActive = false;
            _selected = 0;
            Refresh();
            _tick.Start();
            await LoadThumbnailsAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Video editor failed to open", ex);
            Loading.IsActive = false;
            App.MainWindow.ShowBanner("Cannot edit this video", ex.Message, InfoBarSeverity.Error);
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _tick.Stop();
        _exportCts?.Cancel();
        if (_player != null) { _player.Pause(); _player.Source = null; }
        Player.SetMediaPlayer(null); // releases the file
        _player?.Dispose();
        _player = null;
    }

    public async Task<bool> CanLeaveAsync()
    {
        if (_exportCts != null)
        {
            var r = await UiHelpers.ShowDialogAsync(XamlRoot, "Export in progress", "Leaving cancels the export.", "Cancel export and leave", close: "Stay");
            return r == ContentDialogResult.Primary;
        }
        if (_tl == null || !_tl.IsEdited || _exported) return true;
        var res = await UiHelpers.ShowDialogAsync(XamlRoot, "Discard video edits?",
            "Your cuts have not been exported. The original recording is unchanged either way.", "Discard", close: "Keep editing", destructive: true);
        return res == ContentDialogResult.Primary;
    }

    private async Task LoadThumbnailsAsync()
    {
        try
        {
            var thumbs = await VideoEditing.GetThumbnailsAsync(_item!.Path, ThumbCount, 72);
            foreach (var t in thumbs)
            {
                using var ms = new InMemoryRandomAccessStream();
                await ms.WriteAsync(t.Jpeg.AsBuffer());
                ms.Seek(0);
                var bi = new BitmapImage();
                await bi.SetSourceAsync(ms);
                _thumbs.Add((t.Time, bi));
            }
            Redraw();
        }
        catch (Exception ex) { Log.Warn($"Timeline thumbnails unavailable: {ex.Message}"); }
    }

    private void OnTimelineChanged()
    {
        _exported = false;
        if (_tl != null && _selected >= _tl.Segments.Count) _selected = _tl.Segments.Count - 1;
        Refresh();
    }

    // ---- drawing -------------------------------------------------------------------------------------------

    private void Refresh()
    {
        if (_tl == null) return;
        UndoButton.IsEnabled = _tl.CanUndo;
        RedoButton.IsEnabled = _tl.CanRedo;
        ResetButton.IsEnabled = _tl.IsEdited;
        DeleteButton.IsEnabled = _tl.Segments.Count > 1;
        DirtyDot.Visibility = _tl.IsEdited && !_exported ? Visibility.Visible : Visibility.Collapsed;
        Summary.Text = $"{Fmt(_tl.OutputDuration)} of {Fmt(_tl.SourceDuration)} kept · {_tl.Segments.Count} part(s)"
                       + (_tl.RemovedDuration > TimeSpan.Zero ? $" · {Fmt(_tl.RemovedDuration)} removed" : "");
        var sel = Selected;
        MuteButton.IsEnabled = sel != null;
        MuteText.Text = sel?.Muted == true ? "Unmute part" : "Mute part";
        MuteGlyph.Glyph = sel?.Muted == true ? "" : "";
        MuteAllToggle.IsChecked = _tl.Segments.All(s => s.Muted);
        Redraw();
    }

    private VideoSegment? Selected => _tl != null && _selected >= 0 && _selected < _tl.Segments.Count ? _tl.Segments[_selected] : null;

    private static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss\.f");

    private void Redraw()
    {
        if (_tl == null || TimelineScroller.ActualWidth <= 0) return;
        var c = TimelineCanvas;
        c.Children.Clear();
        double width = X(_tl.SourceDuration) + Pad;
        c.Width = width;
        TimelineHost.Width = width;

        var text = UiHelpers.ThemeBrush("PxTextSecondary", ActualTheme);
        var muted = UiHelpers.ThemeBrush("PxTextMuted", ActualTheme);
        var accent = (Brush)Application.Current.Resources["PxAccent"];

        // Ruler: pick a tick step that keeps labels ~80 px apart.
        double[] steps = { 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 };
        double step = steps.FirstOrDefault(s => s * PxPerSec >= 80, steps[^1]);
        for (double s = 0; s <= Seconds + 1e-6; s += step)
        {
            double x = X(TimeSpan.FromSeconds(s));
            c.Children.Add(new Line { X1 = x, X2 = x, Y1 = RulerHeight - 7, Y2 = RulerHeight, Stroke = muted, StrokeThickness = 1 });
            var fmt = s >= 3600 ? @"h\:mm\:ss" : step < 1 ? @"m\:ss\.f" : @"m\:ss";
            var lbl = new TextBlock { Text = TimeSpan.FromSeconds(s).ToString(fmt), FontSize = 10, Foreground = text };
            Canvas.SetLeft(lbl, x + 3); Canvas.SetTop(lbl, 0);
            c.Children.Add(lbl);
        }

        // Thumbnail strip across the whole source.
        var trackBg = new Rectangle { Width = width - 2 * Pad, Height = TrackHeight, Fill = UiHelpers.ThemeBrush("PxBg0", ActualTheme), RadiusX = 6, RadiusY = 6 };
        Canvas.SetLeft(trackBg, Pad); Canvas.SetTop(trackBg, TrackTop);
        c.Children.Add(trackBg);
        if (_thumbs.Count > 0)
        {
            double slot = _tl.SourceDuration.TotalSeconds / _thumbs.Count * PxPerSec;
            for (int i = 0; i < _thumbs.Count; i++)
            {
                var img = new Image { Source = _thumbs[i].Image, Width = Math.Ceiling(slot), Height = TrackHeight, Stretch = Stretch.UniformToFill, IsHitTestVisible = false };
                Canvas.SetLeft(img, Pad + i * slot); Canvas.SetTop(img, TrackTop);
                c.Children.Add(img);
            }
        }

        // Removed parts: darkened with a "cut" label.
        TimeSpan cursor = TimeSpan.Zero;
        foreach (var gap in Gaps())
        {
            var r = new Rectangle { Width = Math.Max(0, X(gap.End) - X(gap.Start)), Height = TrackHeight, Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0xD0, 0x0D, 0x0D, 0x0F)), IsHitTestVisible = false };
            Canvas.SetLeft(r, X(gap.Start)); Canvas.SetTop(r, TrackTop);
            c.Children.Add(r);
            if (r.Width > 34)
            {
                var cut = new FontIcon { Glyph = "", FontSize = 14, Foreground = muted, IsHitTestVisible = false };
                Canvas.SetLeft(cut, X(gap.Start) + r.Width / 2 - 7); Canvas.SetTop(cut, TrackTop + TrackHeight / 2 - 7);
                c.Children.Add(cut);
            }
        }

        // Kept parts with trim handles.
        for (int i = 0; i < _tl.Segments.Count; i++)
        {
            var s = _tl.Segments[i];
            bool sel = i == _selected;
            double x0 = X(s.Start), x1 = X(s.End);
            var frame = new Border
            {
                Width = Math.Max(2, x1 - x0), Height = TrackHeight, CornerRadius = new CornerRadius(6),
                BorderBrush = sel ? accent : new SolidColorBrush(Windows.UI.Color.FromArgb(0x99, 0xE5, 0xEE, 0xF0)),
                BorderThickness = new Thickness(sel ? 3 : 1.5), Background = new SolidColorBrush(Colors.Transparent), IsHitTestVisible = false,
            };
            Canvas.SetLeft(frame, x0); Canvas.SetTop(frame, TrackTop);
            c.Children.Add(frame);
            foreach (var hx in new[] { x0, x1 - 7 })
            {
                // White grips stay visible on any thumbnail colour; the selected part's grips get a coral outline.
                var h = new Rectangle { Width = 7, Height = TrackHeight - 16, RadiusX = 3, RadiusY = 3, Fill = new SolidColorBrush(Colors.White),
                    Stroke = sel ? accent : new SolidColorBrush(Windows.UI.Color.FromArgb(0x99, 0, 0, 0)), StrokeThickness = sel ? 2 : 1, IsHitTestVisible = false };
                Canvas.SetLeft(h, hx); Canvas.SetTop(h, TrackTop + 8);
                c.Children.Add(h);
            }
            if (s.Muted && x1 - x0 > 30)
            {
                var badge = new Border { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xE0, 0x0D, 0x0D, 0x0F)), CornerRadius = new CornerRadius(4), Padding = new Thickness(4, 2, 4, 2), IsHitTestVisible = false,
                    Child = new FontIcon { Glyph = "", FontSize = 11, Foreground = new SolidColorBrush(Colors.White) } };
                Canvas.SetLeft(badge, x0 + 10); Canvas.SetTop(badge, TrackTop + 6);
                c.Children.Add(badge);
            }
        }

        // Playhead on top.
        _playhead = new Line { Y1 = 0, Y2 = TrackTop + TrackHeight + 4, Stroke = new SolidColorBrush(Colors.White), StrokeThickness = 2, IsHitTestVisible = false };
        _playheadCap = new Polygon { Points = { new Point(-6, 0), new Point(6, 0), new Point(0, 8) }, Fill = new SolidColorBrush(Colors.White), IsHitTestVisible = false };
        c.Children.Add(_playhead);
        c.Children.Add(_playheadCap);
        MovePlayhead();
    }

    private IEnumerable<(TimeSpan Start, TimeSpan End)> Gaps()
    {
        var cursor = TimeSpan.Zero;
        foreach (var s in _tl!.Segments)
        {
            if (s.Start > cursor) yield return (cursor, s.Start);
            cursor = s.End;
        }
        if (cursor < _tl.SourceDuration) yield return (cursor, _tl.SourceDuration);
    }

    private TimeSpan Position => _player?.PlaybackSession.Position ?? TimeSpan.Zero;

    private void MovePlayhead()
    {
        if (_playhead == null || _playheadCap == null) return;
        double x = X(Position);
        _playhead.X1 = _playhead.X2 = x;
        Canvas.SetLeft(_playheadCap, x);
    }

    // ---- playback ------------------------------------------------------------------------------------------

    private bool IsPlaying => _player?.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;

    private void OnTick()
    {
        if (_tl == null || _player == null) return;
        var pos = Position;
        if (IsPlaying && _drag == Drag.None)
        {
            // Preview the edit: jump over removed parts, stop after the last kept part.
            var next = _tl.PlayablePosition(pos);
            if (next == null) { _player.Pause(); Seek(_tl.Segments[^1].End - TimeSpan.FromMilliseconds(40)); }
            else if (next.Value - pos > TimeSpan.FromMilliseconds(30)) Seek(next.Value);
        }
        int idx = _tl.IndexAt(Position);
        bool inGap = idx < 0;
        bool segMuted = idx >= 0 && _tl.Segments[idx].Muted;
        _player.IsMuted = segMuted;
        CutBadge.Visibility = inGap ? Visibility.Visible : Visibility.Collapsed;
        MutedBadge.Visibility = segMuted ? Visibility.Visible : Visibility.Collapsed;
        PlayGlyph.Glyph = IsPlaying ? "" : "";
        TimeText.Text = $"{Fmt(_tl.ToOutputTime(Position))} / {Fmt(_tl.OutputDuration)}";
        SourceTimeText.Text = $"source {Fmt(Position)}";
        MovePlayhead();
        KeepPlayheadVisible();
    }

    private void KeepPlayheadVisible()
    {
        if (!IsPlaying) return;
        double x = X(Position), left = TimelineScroller.HorizontalOffset, view = TimelineScroller.ViewportWidth;
        if (x < left + 20 || x > left + view - 40) TimelineScroller.ChangeView(Math.Max(0, x - view * 0.3), null, null, true);
    }

    private void Seek(TimeSpan t)
    {
        if (_player == null) return;
        _player.PlaybackSession.Position = t;
        MovePlayhead();
    }

    private void TogglePlay()
    {
        if (_player == null || _tl == null) return;
        if (IsPlaying) { _player.Pause(); return; }
        var start = _tl.PlayablePosition(Position) ?? _tl.Segments[0].Start;
        if (_tl.PlayablePosition(Position) == null || Position >= _tl.Segments[^1].End - TimeSpan.FromMilliseconds(60)) start = _tl.Segments[0].Start;
        Seek(start);
        _player.Play();
    }

    // ---- timeline input ------------------------------------------------------------------------------------

    private void Timeline_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_tl == null) return;
        var pt = e.GetCurrentPoint(TimelineCanvas);
        if (!pt.Properties.IsLeftButtonPressed) return;
        Focus(FocusState.Pointer);
        double x = pt.Position.X;
        _drag = Drag.Scrub;
        _dragRecorded = false;
        if (pt.Position.Y >= TrackTop)
        {
            for (int i = 0; i < _tl.Segments.Count; i++)
            {
                var s = _tl.Segments[i];
                if (Math.Abs(x - X(s.Start)) <= HandleHit) { _drag = Drag.TrimStart; _dragIndex = i; break; }
                if (Math.Abs(x - X(s.End)) <= HandleHit) { _drag = Drag.TrimEnd; _dragIndex = i; break; }
            }
        }
        var t = T(x);
        if (_drag == Drag.Scrub)
        {
            int idx = _tl.IndexAt(t);
            if (idx >= 0) _selected = idx;
            _wasPlayingBeforeScrub = IsPlaying;
            _player?.Pause();
            Seek(t);
        }
        else
        {
            _selected = _dragIndex;
            _player?.Pause();
        }
        TimelineHost.CapturePointer(e.Pointer);
        Refresh();
    }

    private void Timeline_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_tl == null) return;
        var pt = e.GetCurrentPoint(TimelineCanvas).Position;
        if (_drag == Drag.None)
        {
            bool overEdge = pt.Y >= TrackTop && _tl.Segments.Any(s => Math.Abs(pt.X - X(s.Start)) <= HandleHit || Math.Abs(pt.X - X(s.End)) <= HandleHit);
            TimelineHost.SetCursor(overEdge ? InputSystemCursorShape.SizeWestEast : InputSystemCursorShape.Arrow);
            return;
        }
        var t = T(pt.X);
        switch (_drag)
        {
            case Drag.Scrub:
                Seek(t);
                break;
            case Drag.TrimStart:
            case Drag.TrimEnd:
                if (!_dragRecorded) { _tl.Checkpoint(); _dragRecorded = true; }
                if (_drag == Drag.TrimStart) { _tl.SetStart(_dragIndex, t, record: false); Seek(_tl.Segments[_dragIndex].Start); }
                else { _tl.SetEnd(_dragIndex, t, record: false); Seek(_tl.Segments[_dragIndex].End - TimeSpan.FromMilliseconds(40)); }
                break;
        }
    }

    private void Timeline_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        TimelineHost.ReleasePointerCapture(e.Pointer);
        if (_drag == Drag.Scrub && _wasPlayingBeforeScrub) TogglePlay();
        _drag = Drag.None;
        Refresh();
    }

    private void Timeline_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control)) return;
        int delta = e.GetCurrentPoint(TimelineScroller).Properties.MouseWheelDelta;
        ZoomSlider.Value = Math.Clamp(ZoomSlider.Value * (delta > 0 ? 1.25 : 0.8), ZoomSlider.Minimum, ZoomSlider.Maximum);
        e.Handled = true;
    }

    private void Zoom_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_tl == null) return;
        Redraw();
        TimelineScroller.ChangeView(Math.Max(0, X(Position) - TimelineScroller.ViewportWidth / 2), null, null, true);
    }

    private void TimelineScroller_SizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    // ---- commands ------------------------------------------------------------------------------------------

    private void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();

    private void Split_Click(object sender, RoutedEventArgs e) => Split();

    private void Split()
    {
        if (_tl == null) return;
        var t = Position;
        if (_tl.SplitAt(t)) _selected = _tl.IndexAt(t);
        else App.MainWindow.ShowBanner("Cannot split here", "Move the playhead inside a kept part, at least 0.2 s from its edges.", InfoBarSeverity.Warning);
    }

    private void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelected();

    private void DeleteSelected()
    {
        if (_tl == null) return;
        int i = _selected >= 0 ? _selected : _tl.IndexAt(Position);
        if (i < 0) return;
        if (!_tl.Delete(i)) { App.MainWindow.ShowBanner("Cannot delete", "At least one part must remain. Use Reset or trim instead.", InfoBarSeverity.Warning); return; }
        _selected = Math.Min(i, _tl.Segments.Count - 1);
        Refresh();
    }

    private void Mute_Click(object sender, RoutedEventArgs e) => ToggleMuteSelected();

    private void ToggleMuteSelected()
    {
        if (_tl == null) return;
        int i = _selected >= 0 ? _selected : _tl.IndexAt(Position);
        if (i >= 0) _tl.ToggleMute(i);
    }

    private void MuteAll_Click(object sender, RoutedEventArgs e) => _tl?.SetAllMuted(MuteAllToggle.IsChecked == true);
    private void Undo_Click(object sender, RoutedEventArgs e) => _tl?.Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => _tl?.Redo();
    private void Reset_Click(object sender, RoutedEventArgs e) { _tl?.Reset(); _selected = 0; Refresh(); }

    private async void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_item != null) App.MainWindow.Navigate(PageKind.Preview, _item.Id);
        await Task.CompletedTask;
    }

    private async void Export_Click(object sender, RoutedEventArgs e) => await ExportAsync();

    private async Task ExportAsync()
    {
        if (_tl == null || _item == null || _exportCts != null) return;
        _player?.Pause();
        var suggested = Path.GetFileNameWithoutExtension(_item.Path) + "_edited";
        var path = await UiHelpers.PickSaveVideoAsync(suggested);
        if (path == null) return;
        if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(_item.Path), StringComparison.OrdinalIgnoreCase))
        {
            App.MainWindow.ShowBanner("Choose another name", "The original recording cannot be overwritten while it is being edited.", InfoBarSeverity.Warning);
            return;
        }

        _exportCts = new CancellationTokenSource();
        ExportOverlay.Visibility = Visibility.Visible;
        ExportProgress.Value = 0;
        ExportText.Text = $"{Fmt(_tl.OutputDuration)} · {_tl.Segments.Count} part(s) → {Path.GetFileName(path)}";
        var progress = new Progress<double>(v => ExportProgress.Value = v);
        try
        {
            await VideoEditing.ExportAsync(_item.Path, _tl.Segments.ToList(), path, progress, _exportCts.Token);
            _exported = true;
            var item = await _library.AddAsync(path, MediaKind.Recording);
            App.MainWindow.ShowBanner("Video exported", Path.GetFileName(path), InfoBarSeverity.Success);
            ExportOverlay.Visibility = Visibility.Collapsed;
            _exportCts = null;
            App.MainWindow.Navigate(PageKind.Preview, item.Id);
            return;
        }
        catch (OperationCanceledException)
        {
            App.MainWindow.ShowBanner("Export cancelled", "Nothing was saved.", InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            Log.Error("Video export failed", ex);
            App.MainWindow.ShowBanner("Export failed", ex.Message, InfoBarSeverity.Error);
        }
        ExportOverlay.Visibility = Visibility.Collapsed;
        _exportCts = null;
        Refresh();
    }

    private void CancelExport_Click(object sender, RoutedEventArgs e) => _exportCts?.Cancel();

    // ---- keyboard ------------------------------------------------------------------------------------------

    private void AddAccelerators()
    {
        void Add(VirtualKey key, VirtualKeyModifiers mods, Action action)
        {
            var a = new KeyboardAccelerator { Key = key, Modifiers = mods };
            a.Invoked += (_, e) => { action(); e.Handled = true; };
            KeyboardAccelerators.Add(a);
        }
        Add(VirtualKey.Z, VirtualKeyModifiers.Control, () => _tl?.Undo());
        Add(VirtualKey.Y, VirtualKeyModifiers.Control, () => _tl?.Redo());
        Add(VirtualKey.Z, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => _tl?.Redo());
        Add(VirtualKey.S, VirtualKeyModifiers.Control, () => _ = ExportAsync());
        KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
    }

    private void Page_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_tl == null || _exportCts != null) return;
        if (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down)) return;
        bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
        switch (e.Key)
        {
            case VirtualKey.Space: TogglePlay(); break;
            case VirtualKey.S: Split(); break;
            case VirtualKey.Delete or VirtualKey.Back: DeleteSelected(); break;
            case VirtualKey.M: ToggleMuteSelected(); break;
            case VirtualKey.Left: Seek(Position - TimeSpan.FromSeconds(shift ? 5 : 1) < TimeSpan.Zero ? TimeSpan.Zero : Position - TimeSpan.FromSeconds(shift ? 5 : 1)); break;
            case VirtualKey.Right: Seek(Position + TimeSpan.FromSeconds(shift ? 5 : 1) > _tl.SourceDuration ? _tl.SourceDuration : Position + TimeSpan.FromSeconds(shift ? 5 : 1)); break;
            case VirtualKey.Home: Seek(_tl.Segments[0].Start); break;
            case VirtualKey.End: Seek(_tl.Segments[^1].End - TimeSpan.FromMilliseconds(40)); break;
            default: return;
        }
        e.Handled = true;
    }
}
