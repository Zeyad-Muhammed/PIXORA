using PIXORA.Core;
using PIXORA.Editor;
using SkiaSharp;

namespace PIXORA.App.Services;

/// <summary>Keeps the open editor document alive while the user navigates between pages, and owns saving it.</summary>
public sealed class EditorSession
{
    private readonly SettingsStore _settings;

    public EditorSession(SettingsStore settings) => _settings = settings;

    public EditorDocument? Document { get; private set; }

    public event Action? DocumentReplaced;
    public event Action<string>? Saved;

    public bool HasUnsavedChanges => Document?.IsDirty == true;

    public void Open(string path) => Replace(EditorDocument.Open(path));

    public void OpenBitmap(SKBitmap bitmap, string? path) => Replace(new EditorDocument(bitmap, path));

    public void Close() => Replace(null);

    private void Replace(EditorDocument? doc)
    {
        var old = Document;
        Document = doc;
        DocumentReplaced?.Invoke();
        old?.Dispose();
    }

    /// <summary>
    /// Saves the flattened image. "Save" writes back to the document's file when it has one in a supported format;
    /// otherwise (or for Save As) a file picker is shown. Returns the saved path, or null if cancelled.
    /// </summary>
    public async Task<string?> SaveAsync(bool saveAs)
    {
        var doc = Document;
        if (doc == null) return null;
        var s = _settings.Current;
        string? path = saveAs ? null : doc.FilePath;
        if (path != null && !NamingTemplate.TryFormatFromExtension(path, out _)) path = null;
        if (path == null)
        {
            var suggested = doc.FilePath != null ? Path.GetFileNameWithoutExtension(doc.FilePath) + "_edited" : NamingTemplate.Expand(s.ScreenshotNameTemplate, DateTime.Now);
            path = await UiHelpers.PickSaveImageAsync(suggested, s.ScreenshotFormat);
            if (path == null) return null;
        }
        NamingTemplate.TryFormatFromExtension(path, out var fmt);
        await Task.Run(() => ImageExporter.SaveDocument(doc, path, fmt, s.ImageQuality));
        doc.FilePath = path;
        doc.MarkSaved();
        await App.Get<MediaLibraryService>().AddAsync(path, MediaKind.Screenshot);
        Saved?.Invoke(path);
        return path;
    }
}
