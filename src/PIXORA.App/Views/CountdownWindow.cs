using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PIXORA.Capture;
using Windows.System;

namespace PIXORA.App.Views;

/// <summary>A small always-on-top countdown. Returns false if the user cancelled.</summary>
internal sealed class CountdownWindow : Window
{
    private readonly TaskCompletionSource<bool> _tcs = new();
    private readonly TextBlock _number;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _remaining;

    private CountdownWindow(MonitorInfo monitor, int seconds, string caption)
    {
        _remaining = seconds;
        Title = "PIXORA countdown";
        _number = new TextBlock
        {
            Text = seconds.ToString(), FontSize = 64, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Colors.White), HorizontalAlignment = HorizontalAlignment.Center,
        };
        var cancel = new Button { Content = "Cancel  (Esc)", Style = (Style)Application.Current.Resources["PxSecondaryButton"], HorizontalAlignment = HorizontalAlignment.Center };
        cancel.Click += (_, _) => Finish(false);
        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = caption, FontSize = 13, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xA3, 0xA3, 0xAA)), HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(_number);
        panel.Children.Add(cancel);
        var root = new Grid
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1D)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xC7, 0x5C, 0x64)), BorderThickness = new Thickness(1),
        };
        root.Children.Add(panel);
        var host = new ContentControl { Content = root, IsTabStop = true, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        host.KeyDown += (_, e) => { if (e.Key == VirtualKey.Escape) Finish(false); };
        Content = host;
        Activated += (_, _) => host.Focus(FocusState.Programmatic);
        Closed += (_, _) => { _timer.Stop(); _tcs.TrySetResult(false); };
        _timer.Tick += (_, _) =>
        {
            _remaining--;
            if (_remaining <= 0) Finish(true);
            else _number.Text = _remaining.ToString();
        };
        WindowChrome.MakeFloating(this, monitor, 220, 190, -1);
        WindowChrome.ExcludeFromCapture(this);
    }

    private void Finish(bool ok)
    {
        _timer.Stop();
        _tcs.TrySetResult(ok);
        Close();
    }

    public static async Task<bool> RunAsync(int seconds, string caption, MonitorInfo? monitor = null)
    {
        if (seconds <= 0) return true;
        monitor ??= ScreenCapture.GetMonitors().First(m => m.IsPrimary);
        var w = new CountdownWindow(monitor, seconds, caption);
        w.Activate();
        w._timer.Start();
        var ok = await w._tcs.Task;
        await Task.Delay(150); // make sure the countdown has left the screen
        return ok;
    }
}
