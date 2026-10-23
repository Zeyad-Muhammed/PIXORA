using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using PIXORA.App.Services;
using PIXORA.Capture;
using SkiaSharp;
using Windows.Foundation;
using Windows.System;

namespace PIXORA.App.Views;

public enum SelectMode { Region, Window }

public sealed record SelectionResult(RECT Rect, IntPtr Window, string? WindowTitle);

/// <summary>
/// The outcome of an interactive selection, plus the frozen monitor images the user was looking at
/// (so a region screenshot contains exactly what was on screen when the overlay opened).
/// </summary>
public sealed class ScreenSelection : IDisposable
{
    public required SelectionResult Result { get; init; }
    public required IReadOnlyList<(MonitorInfo Monitor, SKBitmap Image)> Frozen { get; init; }

    public SKBitmap CropRegion()
    {
        var r = Result.Rect;
        foreach (var (m, img) in Frozen)
        {
            if (r.Left >= m.Bounds.Left && r.Top >= m.Bounds.Top && r.Right <= m.Bounds.Right && r.Bottom <= m.Bounds.Bottom)
                return ScreenCapture.Crop(img, new SKRectI(r.Left - m.Bounds.Left, r.Top - m.Bounds.Top, r.Right - m.Bounds.Left, r.Bottom - m.Bounds.Top));
        }
        return ScreenCapture.CaptureRect(r.Left, r.Top, r.Width, r.Height);
    }

    public void Dispose()
    {
        foreach (var (_, img) in Frozen) img.Dispose();
    }
}

public static class ScreenSelector
{
    private static bool _active;

    /// <summary>Shows a frozen-screen overlay on every monitor and waits for the user to pick a region or window. Null = cancelled.</summary>
    public static async Task<ScreenSelection?> SelectAsync(SelectMode mode, string hint, IEnumerable<IntPtr> excludeWindows)
    {
        if (_active) return null;
        _active = true;
        var frozen = new List<(MonitorInfo, SKBitmap)>();
        var overlays = new List<SelectionOverlayWindow>();
        try
        {
            var monitors = ScreenCapture.GetMonitors();
            foreach (var m in monitors) frozen.Add((m, ScreenCapture.CaptureMonitor(m)));
            var windows = mode == SelectMode.Window ? ScreenCapture.GetCapturableWindows(excludeWindows) : Array.Empty<WindowInfo>();

            var tcs = new TaskCompletionSource<SelectionResult?>();
            foreach (var (m, img) in frozen)
            {
                var w = new SelectionOverlayWindow(m, img, mode, windows, hint, r => tcs.TrySetResult(r));
                overlays.Add(w);
            }
            Win32Cursor(out var cx, out var cy);
            var focusFirst = overlays.FirstOrDefault(o => o.Contains(cx, cy)) ?? overlays.First();
            foreach (var o in overlays) if (o != focusFirst) o.Activate();
            focusFirst.Activate();

            var result = await tcs.Task;
            foreach (var o in overlays) o.Close();
            overlays.Clear();
            if (result == null) { foreach (var (_, img) in frozen) img.Dispose(); return null; }
            await Task.Delay(120); // let the overlay disappear before any live capture
            return new ScreenSelection { Result = result, Frozen = frozen };
        }
        catch
        {
            foreach (var o in overlays) o.Close();
            foreach (var (_, img) in frozen) img.Dispose();
            throw;
        }
        finally { _active = false; }
    }

    private static void Win32Cursor(out int x, out int y)
    {
        Interop.Win32.GetCursorPos(out var p);
        x = p.X; y = p.Y;
    }
}

internal sealed class SelectionOverlayWindow : Window
{
    private enum Drag { None, Create, Move, Resize }

    private readonly MonitorInfo _mon;
    private readonly SelectMode _mode;
    private readonly IReadOnlyList<WindowInfo> _windows;
    private readonly Action<SelectionResult?> _done;
    private readonly Canvas _canvas = new();
    private readonly Rectangle[] _dim = new Rectangle[4];
    private readonly Rectangle _sel;
    private readonly Rectangle[] _handles = new Rectangle[8];
    private readonly Border _sizePill;
    private readonly TextBlock _sizeText = new() { FontSize = 12, Foreground = new SolidColorBrush(Colors.White), FontFamily = new FontFamily("Cascadia Mono,Consolas") };
    private readonly StackPanel _toolbar;
    private readonly Line _crossH, _crossV;
    private readonly ContentControl _host;
    private Rect? _selection;     // DIP, local to this monitor
    private WindowInfo? _hoverWindow;
    private Drag _drag;
    private int _handleIndex;
    private Point _dragStart;
    private Rect _dragOrigin;
    private bool _finished;
    private double Scale => _host.XamlRoot?.RasterizationScale ?? _mon.DpiScale;

    public SelectionOverlayWindow(MonitorInfo mon, SKBitmap frozen, SelectMode mode, IReadOnlyList<WindowInfo> windows, string hint, Action<SelectionResult?> done)
    {
        _mon = mon; _mode = mode; _windows = windows; _done = done;
        Title = "PIXORA selection";

        var image = new Image { Source = UiHelpers.ToWriteable(frozen), Stretch = Stretch.Fill };
        var accent = (SolidColorBrush)Application.Current.Resources["PxAccent"];
        var scrim = new SolidColorBrush(Windows.UI.Color.FromArgb(0x8C, 0, 0, 0));
        for (int i = 0; i < 4; i++) { _dim[i] = new Rectangle { Fill = scrim, IsHitTestVisible = false }; _canvas.Children.Add(_dim[i]); }
        _sel = new Rectangle { Stroke = accent, StrokeThickness = 1.5, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        _canvas.Children.Add(_sel);
        for (int i = 0; i < 8; i++)
        {
            _handles[i] = new Rectangle { Width = 9, Height = 9, Fill = new SolidColorBrush(Colors.White), Stroke = accent, StrokeThickness = 1.5, RadiusX = 2, RadiusY = 2, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            _canvas.Children.Add(_handles[i]);
        }
        var crossBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x99, 0xC7, 0x5C, 0x64));
        _crossH = new Line { Stroke = crossBrush, StrokeThickness = 1, IsHitTestVisible = false, StrokeDashArray = new DoubleCollection { 4, 3 } };
        _crossV = new Line { Stroke = crossBrush, StrokeThickness = 1, IsHitTestVisible = false, StrokeDashArray = new DoubleCollection { 4, 3 } };
        _canvas.Children.Add(_crossH); _canvas.Children.Add(_crossV);

        _sizePill = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xE6, 0x22, 0x22, 0x26)), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 3, 8, 3), Child = _sizeText, IsHitTestVisible = false, Visibility = Visibility.Collapsed,
        };
        _canvas.Children.Add(_sizePill);

        var confirm = new Button { Content = mode == SelectMode.Region ? "Capture" : "Select", Style = (Style)Application.Current.Resources["PxPrimaryButton"] };
        confirm.Click += (_, _) => Confirm();
        var cancel = new Button { Content = "Cancel", Style = (Style)Application.Current.Resources["PxSecondaryButton"] };
        cancel.Click += (_, _) => Finish(null);
        _toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Visibility = Visibility.Collapsed, Padding = new Thickness(6), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xF0, 0x1A, 0x1A, 0x1D)), CornerRadius = new CornerRadius(10) };
        _toolbar.Children.Add(confirm);
        _toolbar.Children.Add(cancel);
        _canvas.Children.Add(_toolbar);

        var hintPill = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xF0, 0x1A, 0x1A, 0x1D)), BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x30, 0x30, 0x35)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(999), Padding = new Thickness(16, 8, 16, 8),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 24, 0, 0), IsHitTestVisible = false,
            Child = new TextBlock { Text = hint + "   ·   Enter to confirm   ·   Esc to cancel", Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xE5, 0xEE, 0xF0)), FontSize = 13 },
        };

        var grid = new CursorGrid { Background = new SolidColorBrush(Colors.Black) };
        grid.Children.Add(image);
        grid.Children.Add(_canvas);
        grid.Children.Add(hintPill);
        _canvas.Background = new SolidColorBrush(Colors.Transparent); // receive pointer input everywhere
        _host = new ContentControl
        {
            Content = grid, IsTabStop = true, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
            UseSystemFocusVisuals = false,
        };
        Content = _host;
        _host.KeyDown += OnKeyDown;
        _canvas.PointerPressed += OnPressed;
        _canvas.PointerMoved += OnMoved;
        _canvas.PointerReleased += OnReleased;
        _canvas.DoubleTapped += (_, e) => { if (_selection is { } s && s.Contains(e.GetPosition(_canvas))) Confirm(); };
        _canvas.SizeChanged += (_, _) => Layout();
        Activated += (_, _) => _host.Focus(FocusState.Programmatic);
        Closed += (_, _) => { if (!_finished) { _finished = true; _done(null); } };

        WindowChrome.CoverMonitor(this, mon);
        _canvas.Loaded += (_, _) => { grid.SetCursor(InputSystemCursorShape.Cross); Layout(); };
    }

    public bool Contains(int x, int y) => x >= _mon.Bounds.Left && x < _mon.Bounds.Right && y >= _mon.Bounds.Top && y < _mon.Bounds.Bottom;

    private Point ToPhysical(Point dip) => new(_mon.Bounds.Left + dip.X * Scale, _mon.Bounds.Top + dip.Y * Scale);
    private Point ToDip(int x, int y) => new((x - _mon.Bounds.Left) / Scale, (y - _mon.Bounds.Top) / Scale);

    private RECT PhysicalRect(Rect r)
    {
        var a = ToPhysical(new Point(r.Left, r.Top));
        var b = ToPhysical(new Point(r.Right, r.Bottom));
        return new RECT((int)Math.Round(a.X), (int)Math.Round(a.Y), (int)Math.Round(b.X), (int)Math.Round(b.Y));
    }

    private void Finish(SelectionResult? r)
    {
        if (_finished) return;
        _finished = true;
        _done(r);
    }

    private void Confirm()
    {
        if (_mode == SelectMode.Window)
        {
            if (_hoverWindow != null) Finish(new SelectionResult(_hoverWindow.Bounds, _hoverWindow.Handle, _hoverWindow.Title));
            return;
        }
        if (_selection is not { } s) return;
        var r = PhysicalRect(s);
        if (r.Width < 2 || r.Height < 2) return;
        Finish(new SelectionResult(r, IntPtr.Zero, null));
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape: Finish(null); e.Handled = true; return;
            case VirtualKey.Enter: Confirm(); e.Handled = true; return;
        }
        if (_selection is not { } s || _mode != SelectMode.Region) return;
        bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        double step = 1 / Scale; // one physical pixel
        double dx = 0, dy = 0;
        if (e.Key == VirtualKey.Left) dx = -step; else if (e.Key == VirtualKey.Right) dx = step;
        else if (e.Key == VirtualKey.Up) dy = -step; else if (e.Key == VirtualKey.Down) dy = step;
        else return;
        _selection = shift ? new Rect(s.X, s.Y, Math.Max(step, s.Width + dx), Math.Max(step, s.Height + dy)) : new Rect(s.X + dx, s.Y + dy, s.Width, s.Height);
        ClampSelection();
        Layout();
        e.Handled = true;
    }

    private IEnumerable<Point> HandlePoints(Rect r)
    {
        yield return new(r.Left, r.Top); yield return new(r.Left + r.Width / 2, r.Top); yield return new(r.Right, r.Top);
        yield return new(r.Right, r.Top + r.Height / 2); yield return new(r.Right, r.Bottom); yield return new(r.Left + r.Width / 2, r.Bottom);
        yield return new(r.Left, r.Bottom); yield return new(r.Left, r.Top + r.Height / 2);
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(_canvas);
        if (p.Properties.IsRightButtonPressed) { Finish(null); return; }
        if (!p.Properties.IsLeftButtonPressed) return;
        _host.Focus(FocusState.Pointer);
        var pos = p.Position;

        if (_mode == SelectMode.Window) { Confirm(); return; }

        _dragStart = pos;
        if (_selection is { } s)
        {
            int i = 0;
            foreach (var h in HandlePoints(s))
            {
                if (Math.Abs(h.X - pos.X) <= 8 && Math.Abs(h.Y - pos.Y) <= 8) { _drag = Drag.Resize; _handleIndex = i; _dragOrigin = s; _canvas.CapturePointer(e.Pointer); return; }
                i++;
            }
            if (s.Contains(pos)) { _drag = Drag.Move; _dragOrigin = s; _canvas.CapturePointer(e.Pointer); return; }
        }
        _drag = Drag.Create;
        _selection = new Rect(pos, pos);
        _toolbar.Visibility = Visibility.Collapsed;
        _canvas.CapturePointer(e.Pointer);
        Layout();
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(_canvas).Position;
        double w = _canvas.ActualWidth, h = _canvas.ActualHeight;
        pos = new Point(Math.Clamp(pos.X, 0, w), Math.Clamp(pos.Y, 0, h));

        if (_mode == SelectMode.Window)
        {
            var phys = ToPhysical(pos);
            var hit = ScreenCapture.WindowAt(_windows, (int)phys.X, (int)phys.Y);
            if (hit != _hoverWindow) { _hoverWindow = hit; Layout(); }
            return;
        }

        _crossH.X1 = 0; _crossH.X2 = w; _crossH.Y1 = _crossH.Y2 = pos.Y;
        _crossV.Y1 = 0; _crossV.Y2 = h; _crossV.X1 = _crossV.X2 = pos.X;
        bool showCross = _selection == null || _drag == Drag.Create;
        _crossH.Visibility = _crossV.Visibility = showCross ? Visibility.Visible : Visibility.Collapsed;

        switch (_drag)
        {
            case Drag.Create:
                _selection = new Rect(_dragStart, pos);
                break;
            case Drag.Move:
                var o = _dragOrigin;
                double nx = Math.Clamp(o.X + pos.X - _dragStart.X, 0, w - o.Width);
                double ny = Math.Clamp(o.Y + pos.Y - _dragStart.Y, 0, h - o.Height);
                _selection = new Rect(nx, ny, o.Width, o.Height);
                break;
            case Drag.Resize:
                _selection = ResizeFromHandle(_dragOrigin, _handleIndex, pos);
                break;
            default:
                if (_selection == null) { UpdateSizeLabelForCursor(pos); return; }
                break;
        }
        Layout();
    }

    private static Rect ResizeFromHandle(Rect o, int i, Point p)
    {
        double l = o.Left, t = o.Top, r = o.Right, b = o.Bottom;
        if (i is 0 or 6 or 7) l = p.X;
        if (i is 2 or 3 or 4) r = p.X;
        if (i is 0 or 1 or 2) t = p.Y;
        if (i is 4 or 5 or 6) b = p.Y;
        return new Rect(new Point(Math.Min(l, r), Math.Min(t, b)), new Point(Math.Max(l, r), Math.Max(t, b)));
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        _canvas.ReleasePointerCapture(e.Pointer);
        if (_drag == Drag.Create && _selection is { } s && (s.Width < 4 || s.Height < 4))
        {
            // A click without a drag selects the window under the cursor as the region.
            var phys = ToPhysical(e.GetCurrentPoint(_canvas).Position);
            var hit = ScreenCapture.WindowAt(ScreenCapture.GetCapturableWindows(new[] { WindowChrome.Handle(this) }), (int)phys.X, (int)phys.Y);
            if (hit != null)
            {
                var a = ToDip(hit.Bounds.Left, hit.Bounds.Top);
                var b = ToDip(hit.Bounds.Right, hit.Bounds.Bottom);
                _selection = new Rect(a, b);
                ClampSelection();
            }
            else _selection = null;
        }
        _drag = Drag.None;
        Layout();
    }

    private void ClampSelection()
    {
        if (_selection is not { } s) return;
        double w = _canvas.ActualWidth, h = _canvas.ActualHeight;
        double l = Math.Clamp(s.Left, 0, w), t = Math.Clamp(s.Top, 0, h), r = Math.Clamp(s.Right, 0, w), b = Math.Clamp(s.Bottom, 0, h);
        _selection = r - l < 1 || b - t < 1 ? null : new Rect(l, t, r - l, b - t);
    }

    private void UpdateSizeLabelForCursor(Point pos)
    {
        var phys = ToPhysical(pos);
        _sizeText.Text = $"{(int)phys.X}, {(int)phys.Y}";
        _sizePill.Visibility = Visibility.Visible;
        Canvas.SetLeft(_sizePill, pos.X + 14);
        Canvas.SetTop(_sizePill, pos.Y + 14);
    }

    private void SetRect(FrameworkElement el, double x, double y, double w, double h)
    {
        Canvas.SetLeft(el, x); Canvas.SetTop(el, y);
        el.Width = Math.Max(0, w); el.Height = Math.Max(0, h);
    }

    private void Layout()
    {
        double W = _canvas.ActualWidth, H = _canvas.ActualHeight;
        Rect? r = _selection;
        if (_mode == SelectMode.Window && _hoverWindow != null)
        {
            var a = ToDip(_hoverWindow.Bounds.Left, _hoverWindow.Bounds.Top);
            var b = ToDip(_hoverWindow.Bounds.Right, _hoverWindow.Bounds.Bottom);
            var raw = new Rect(a, b);
            double l = Math.Max(0, raw.Left), t = Math.Max(0, raw.Top);
            r = new Rect(l, t, Math.Max(0, Math.Min(W, raw.Right) - l), Math.Max(0, Math.Min(H, raw.Bottom) - t));
        }

        if (r is not { } s || s.Width <= 0 || s.Height <= 0)
        {
            SetRect(_dim[0], 0, 0, W, H);
            for (int i = 1; i < 4; i++) _dim[i].Width = _dim[i].Height = 0;
            _sel.Visibility = Visibility.Collapsed;
            foreach (var hnd in _handles) hnd.Visibility = Visibility.Collapsed;
            _toolbar.Visibility = Visibility.Collapsed;
            if (_mode == SelectMode.Window) _sizePill.Visibility = Visibility.Collapsed;
            return;
        }

        SetRect(_dim[0], 0, 0, W, s.Top);
        SetRect(_dim[1], 0, s.Bottom, W, H - s.Bottom);
        SetRect(_dim[2], 0, s.Top, s.Left, s.Height);
        SetRect(_dim[3], s.Right, s.Top, W - s.Right, s.Height);
        SetRect(_sel, s.Left, s.Top, s.Width, s.Height);
        _sel.Visibility = Visibility.Visible;

        bool handles = _mode == SelectMode.Region && _drag != Drag.Create;
        int k = 0;
        foreach (var p in HandlePoints(s))
        {
            _handles[k].Visibility = handles ? Visibility.Visible : Visibility.Collapsed;
            Canvas.SetLeft(_handles[k], p.X - 4.5); Canvas.SetTop(_handles[k], p.Y - 4.5);
            k++;
        }

        if (_mode == SelectMode.Window)
            _sizeText.Text = $"{_hoverWindow!.Title}  ·  {_hoverWindow.Bounds.Width} × {_hoverWindow.Bounds.Height}";
        else
        {
            var pr = PhysicalRect(s);
            _sizeText.Text = $"{pr.Width} × {pr.Height}";
        }
        _sizePill.Visibility = Visibility.Visible;
        Canvas.SetLeft(_sizePill, s.Left);
        Canvas.SetTop(_sizePill, s.Top >= 30 ? s.Top - 28 : s.Top + 6);

        if (_mode == SelectMode.Region && _drag == Drag.None)
        {
            _toolbar.Visibility = Visibility.Visible;
            _toolbar.UpdateLayout();
            double tw = Math.Max(_toolbar.ActualWidth, 180), th = Math.Max(_toolbar.ActualHeight, 48);
            double tx = Math.Clamp(s.Right - tw, 4, Math.Max(4, W - tw - 4));
            double ty = s.Bottom + 8 + th < H ? s.Bottom + 8 : (s.Top - th - 8 > 0 ? s.Top - th - 8 : s.Bottom - th - 8);
            Canvas.SetLeft(_toolbar, tx); Canvas.SetTop(_toolbar, ty);
        }
        else _toolbar.Visibility = Visibility.Collapsed;
    }
}
