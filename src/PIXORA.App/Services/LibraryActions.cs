using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PIXORA.Capture;
using PIXORA.Core;

namespace PIXORA.App.Services;

/// <summary>Shared item actions used by Home, Library and Preview.</summary>
public static class LibraryActions
{
    public static async Task OpenAsync(MediaItem item, XamlRoot root)
    {
        if (!File.Exists(item.Path))
        {
            var r = await UiHelpers.ShowDialogAsync(root, "File not found",
                $"\"{item.Name}\" was moved or deleted outside PIXORA. Remove it from the library? (Nothing on disk is touched.)", "Remove entry");
            if (r == ContentDialogResult.Primary) await App.Get<MediaLibraryService>().RemoveEntriesAsync(new[] { item.Id });
            return;
        }
        if (item.Kind == MediaKind.Recording)
        {
            App.MainWindow.Navigate(PageKind.Preview, item.Id);
            return;
        }
        if (!await App.MainWindow.ConfirmDiscardEditorChangesAsync()) return;
        try
        {
            App.Get<EditorSession>().Open(item.Path);
            App.MainWindow.Navigate(PageKind.Editor);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            App.MainWindow.ShowBanner("Cannot open image", ex.Message, InfoBarSeverity.Error);
        }
    }

    public static async Task<bool> RenameAsync(MediaItem item, XamlRoot root)
    {
        var name = await UiHelpers.PromptAsync(root, "Rename", Path.GetFileNameWithoutExtension(item.Path));
        if (string.IsNullOrWhiteSpace(name) || name == Path.GetFileNameWithoutExtension(item.Path)) return false;
        try
        {
            await App.Get<MediaLibraryService>().RenameAsync(item.Id, name);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or KeyNotFoundException)
        {
            App.MainWindow.ShowBanner("Rename failed", ex.Message, InfoBarSeverity.Error);
            return false;
        }
    }

    public static void Copy(IReadOnlyList<MediaItem> items)
    {
        var existing = items.Where(i => File.Exists(i.Path)).ToList();
        if (existing.Count == 0) { App.MainWindow.ShowBanner("Nothing to copy", "The selected files are missing.", InfoBarSeverity.Warning); return; }
        try
        {
            // A single screenshot goes on the clipboard as an image (pastes into chat/docs); anything else as files.
            if (existing.Count == 1 && existing[0].Kind == MediaKind.Screenshot)
            {
                using var bmp = SkiaSharp.SKBitmap.Decode(existing[0].Path);
                if (bmp != null) { ClipboardService.SetImage(bmp); App.MainWindow.ShowBanner("Copied", "Image copied to the clipboard.", InfoBarSeverity.Success); return; }
            }
            ClipboardService.SetFiles(existing.Select(i => i.Path));
            App.MainWindow.ShowBanner("Copied", $"{existing.Count} file(s) copied. Paste them in File Explorer or another app.", InfoBarSeverity.Success);
        }
        catch (CaptureException ex) { App.MainWindow.ShowBanner("Copy failed", ex.Message, InfoBarSeverity.Error); }
    }

    /// <summary>Copies files to a folder the user chooses. Never moves or alters the originals.</summary>
    public static async Task ExportAsync(IReadOnlyList<MediaItem> items)
    {
        var existing = items.Where(i => File.Exists(i.Path)).ToList();
        if (existing.Count == 0) return;
        var folder = await UiHelpers.PickFolderAsync();
        if (folder == null) return;
        int done = 0;
        var errors = new List<string>();
        await Task.Run(() =>
        {
            foreach (var it in existing)
            {
                try
                {
                    var dest = NamingTemplate.UniquePath(folder, Path.GetFileNameWithoutExtension(it.Path), Path.GetExtension(it.Path));
                    File.Copy(it.Path, dest);
                    done++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add($"{it.Name}: {ex.Message}"); }
            }
        });
        if (errors.Count == 0) App.MainWindow.ShowBanner("Exported", $"{done} file(s) copied to {folder}", InfoBarSeverity.Success);
        else App.MainWindow.ShowBanner("Export incomplete", string.Join("\n", errors.Take(3)), InfoBarSeverity.Error);
    }
}
