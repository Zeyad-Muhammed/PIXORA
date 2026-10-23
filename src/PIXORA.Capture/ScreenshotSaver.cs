using PIXORA.Core;
using PIXORA.Editor;
using SkiaSharp;

namespace PIXORA.Capture;

/// <summary>Writes captured screenshots into the user's folder with the configured name template and format.</summary>
public static class ScreenshotSaver
{
    public static string Save(SKBitmap bitmap, AppSettings settings, DateTime? now = null)
    {
        var dir = settings.ScreenshotDir;
        var problem = SettingsValidator.ProbeWritable(dir);
        if (problem != null) throw new CaptureException(problem);

        var baseName = NamingTemplate.Expand(settings.ScreenshotNameTemplate, now ?? DateTime.Now);
        var path = NamingTemplate.UniquePath(dir, baseName, NamingTemplate.Extension(settings.ScreenshotFormat));
        try
        {
            ImageExporter.Save(bitmap, path, settings.ScreenshotFormat, settings.ImageQuality);
        }
        catch (IOException ex) when (IsDiskFull(ex))
        {
            throw new CaptureException("There is not enough free disk space to save the screenshot.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CaptureException($"The screenshot could not be saved: {ex.Message}", ex);
        }
        return path;
    }

    private static bool IsDiskFull(IOException ex) => (ex.HResult & 0xFFFF) is 112 or 39;
}
