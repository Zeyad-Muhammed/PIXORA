using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using PIXORA.App.Services;
using PIXORA.Capture;
using PIXORA.Recording;

namespace PIXORA.App.Views;

/// <summary>
/// Floating recording controls. Every indicator is driven by the recorder's real state and clock,
/// so the HUD never claims to be recording when it is not. Excluded from the capture itself.
/// </summary>
internal sealed class RecordingHud : Window
{
    private readonly RecordingController _rec;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly Ellipse _dot;
    private readonly TextBlock _status, _time;
    private readonly Button _pause;
    private readonly FontIcon _pauseIcon;
    private readonly Rectangle _sysLevel, _micLevel;
    private readonly StackPanel _meters;
    private bool _blink;

    public RecordingHud(RecordingController rec, MonitorInfo monitor)
    {
        _rec = rec;
        Title = "PIXORA recording";
        var accent = (SolidColorBrush)Application.Current.Resources["PxAccent"];
        var text = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xE5, 0xEE, 0xF0));
        var muted = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xA3, 0xA3, 0xAA));

        _dot = new Ellipse { Width = 10, Height = 10, Fill = accent, VerticalAlignment = VerticalAlignment.Center };
        _status = new TextBlock { Text = "REC", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, CharacterSpacing = 60, Foreground = accent, VerticalAlignment = VerticalAlignment.Center };
        _time = new TextBlock { Text = "00:00", FontSize = 16, FontFamily = new FontFamily("Cascadia Mono,Consolas"), Foreground = text, VerticalAlignment = VerticalAlignment.Center, MinWidth = 64 };

        Rectangle Meter() => new() { Height = 4, Width = 0, Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x6F, 0xDB, 0x9C)), RadiusX = 2, RadiusY = 2, HorizontalAlignment = HorizontalAlignment.Left };
        _sysLevel = Meter(); _micLevel = Meter();
        _meters = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Width = 56 };
        if (rec.HasSystemAudio) _meters.Children.Add(Labeled("SYS", _sysLevel, muted));
        if (rec.HasMicrophone) _meters.Children.Add(Labeled("MIC", _micLevel, muted));

        _pauseIcon = new FontIcon { Glyph = "", FontSize = 14 };
        _pause = new Button { Content = _pauseIcon, Style = (Style)Application.Current.Resources["PxGhostButton"], Padding = new Thickness(8) };
        ToolTipService.SetToolTip(_pause, "Pause");
        _pause.Click += (_, _) => _rec.TogglePause();
        var stop = new Button { Style = (Style)Application.Current.Resources["PxPrimaryButton"], Padding = new Thickness(12, 6, 12, 6), MinHeight = 32 };
        var stopContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        stopContent.Children.Add(new FontIcon { Glyph = "", FontSize = 12 });
        stopContent.Children.Add(new TextBlock { Text = "Stop" });
        stop.Content = stopContent;
        stop.Click += async (_, _) => await _rec.StopAsync();

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Padding = new Thickness(14, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(_dot); row.Children.Add(_status); row.Children.Add(_time);
        if (_meters.Children.Count > 0) row.Children.Add(_meters);
        row.Children.Add(_pause); row.Children.Add(stop);

        Content = new Grid
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x22, 0x22, 0x26)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x30, 0x30, 0x35)), BorderThickness = new Thickness(1),
            Children = { row },
        };
        WindowChrome.MakeFloating(this, monitor, _meters.Children.Count > 0 ? 360 : 290, 52, 12);
        WindowChrome.ExcludeFromCapture(this);
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
        Refresh();
    }

    private static StackPanel Labeled(string label, Rectangle bar, Brush muted)
    {
        var track = new Grid { Height = 4, Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x30, 0x30, 0x35)), CornerRadius = new CornerRadius(2), Width = 30 };
        track.Children.Add(bar);
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        sp.Children.Add(new TextBlock { Text = label, FontSize = 9, Foreground = muted, Width = 22 });
        sp.Children.Add(track);
        return sp;
    }

    private void Refresh()
    {
        var state = _rec.State;
        _time.Text = UiHelpers.FormatDuration(_rec.Elapsed.TotalSeconds);
        switch (state)
        {
            case RecorderState.Recording:
                _blink = !_blink;
                _dot.Opacity = _blink ? 1 : 0.35;
                _status.Text = "REC";
                _pauseIcon.Glyph = "";
                ToolTipService.SetToolTip(_pause, "Pause");
                _pause.IsEnabled = true;
                break;
            case RecorderState.Paused:
                _dot.Opacity = 0.35;
                _status.Text = "PAUSED";
                _pauseIcon.Glyph = "";
                ToolTipService.SetToolTip(_pause, "Resume");
                _pause.IsEnabled = true;
                break;
            case RecorderState.Stopping:
                _dot.Opacity = 0.35;
                _status.Text = "SAVING";
                _pause.IsEnabled = false;
                break;
            default:
                _status.Text = "STOPPED";
                _dot.Opacity = 0.2;
                _pause.IsEnabled = false;
                break;
        }
        _sysLevel.Width = Math.Clamp(_rec.SystemLevel, 0, 1) * 30;
        _micLevel.Width = Math.Clamp(_rec.MicrophoneLevel, 0, 1) * 30;
    }
}
