using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using PIXORA.Core;
using SkiaSharp;
using Windows.Storage.Pickers;

namespace PIXORA.App.Services;

public static class UiHelpers
{
    /// <summary>Copies a Skia bitmap into a XAML WriteableBitmap, reusing <paramref name="target"/> when the size matches.</summary>
    public static WriteableBitmap ToWriteable(SKBitmap bmp, WriteableBitmap? target = null)
    {
        if (target == null || target.PixelWidth != bmp.Width || target.PixelHeight != bmp.Height)
            target = new WriteableBitmap(bmp.Width, bmp.Height);
        SKBitmap src = bmp;
        SKBitmap? converted = null;
        if (bmp.ColorType != SKColorType.Bgra8888) src = converted = bmp.Copy(SKColorType.Bgra8888);
        try
        {
            using var stream = target.PixelBuffer.AsStream();
            stream.Position = 0;
            var span = src.GetPixelSpan();
            if (src.RowBytes == src.Width * 4) stream.Write(span);
            else for (int y = 0; y < src.Height; y++) stream.Write(span.Slice(y * src.RowBytes, src.Width * 4));
        }
        finally { converted?.Dispose(); }
        target.Invalidate();
        return target;
    }

    /// <summary>Resolves a PIXORA theme brush for a specific element theme (Application.Resources alone follows the app theme).</summary>
    public static Microsoft.UI.Xaml.Media.Brush ThemeBrush(string key, ElementTheme theme)
    {
        var name = theme == ElementTheme.Light ? "Light" : "Dark";
        foreach (var md in Application.Current.Resources.MergedDictionaries)
            if (md.ThemeDictionaries.TryGetValue(name, out var d) && d is ResourceDictionary rd && rd.TryGetValue(key, out var v) && v is Microsoft.UI.Xaml.Media.Brush b)
                return b;
        return (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[key];
    }

    public static BitmapImage? LoadThumb(string? path, int decodeWidth = 360)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        return new BitmapImage(new Uri(path)) { DecodePixelWidth = decodeWidth };
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:0.##} GB",
    };

    public static string FormatDuration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
    }

    public static string FormatRelative(DateTime utc)
    {
        var local = utc.ToLocalTime();
        var d = DateTime.Now - local;
        if (d.TotalMinutes < 1) return "Just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}m ago";
        if (local.Date == DateTime.Today) return $"Today, {local:HH:mm}";
        if (local.Date == DateTime.Today.AddDays(-1)) return $"Yesterday, {local:HH:mm}";
        return local.ToString("MMM d, HH:mm");
    }

    public static void RevealInExplorer(string path)
    {
        try
        {
            if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (Directory.Exists(Path.GetDirectoryName(path))) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Path.GetDirectoryName(path)}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warn($"Reveal failed: {ex.Message}"); }
    }

    public static void OpenWithShell(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"Open failed: {ex.Message}"); }
    }

    public static void InitPicker(object picker)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
    }

    public static async Task<string?> PickFolderAsync()
    {
        var p = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        p.FileTypeFilter.Add("*");
        InitPicker(p);
        var f = await p.PickSingleFolderAsync();
        return f?.Path;
    }

    public static async Task<string?> PickImageAsync()
    {
        var p = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary, ViewMode = PickerViewMode.Thumbnail };
        foreach (var e in new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp" }) p.FileTypeFilter.Add(e);
        InitPicker(p);
        var f = await p.PickSingleFileAsync();
        return f?.Path;
    }

    public static async Task<string?> PickSaveImageAsync(string suggestedName, ImageFormat preferred)
    {
        var p = new FileSavePicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary, SuggestedFileName = suggestedName };
        var choices = new List<(string, string)> { ("PNG image", ".png"), ("JPEG image", ".jpg"), ("WebP image", ".webp") };
        var first = choices.FindIndex(c => c.Item2 == NamingTemplate.Extension(preferred));
        if (first > 0) { var c = choices[first]; choices.RemoveAt(first); choices.Insert(0, c); }
        foreach (var (label, ext) in choices) p.FileTypeChoices.Add(label, new List<string> { ext });
        InitPicker(p);
        var f = await p.PickSaveFileAsync();
        return f?.Path;
    }

    public static async Task<string?> PickSaveVideoAsync(string suggestedName)
    {
        var p = new FileSavePicker { SuggestedStartLocation = PickerLocationId.VideosLibrary, SuggestedFileName = suggestedName };
        p.FileTypeChoices.Add("MP4 video", new List<string> { ".mp4" });
        InitPicker(p);
        var f = await p.PickSaveFileAsync();
        return f?.Path;
    }

    public static async Task<ContentDialogResult> ShowDialogAsync(XamlRoot root, string title, string message, string primary, string? secondary = null, string close = "Cancel", bool destructive = false)
    {
        var dlg = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primary,
            CloseButtonText = close,
            DefaultButton = destructive ? ContentDialogButton.Close : ContentDialogButton.Primary,
            RequestedTheme = (App.MainWindow.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Dark,
        };
        if (secondary != null) dlg.SecondaryButtonText = secondary;
        return await dlg.ShowAsync();
    }

    public static async Task<string?> PromptAsync(XamlRoot root, string title, string initial, string primary = "Rename")
    {
        var box = new TextBox { Text = initial, SelectionStart = 0, SelectionLength = initial.Length, MinWidth = 320 };
        var dlg = new ContentDialog
        {
            XamlRoot = root, Title = title, Content = box, PrimaryButtonText = primary, CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = (App.MainWindow.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Dark,
        };
        dlg.Opened += (_, _) => box.Focus(FocusState.Programmatic);
        return await dlg.ShowAsync() == ContentDialogResult.Primary ? box.Text.Trim() : null;
    }
}
