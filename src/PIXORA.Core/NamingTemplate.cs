using System.Text.RegularExpressions;

namespace PIXORA.Core;

/// <summary>Expands file-name templates and guarantees a collision-free path.</summary>
public static class NamingTemplate
{
    public static readonly Regex TokenRegex = new(@"\{(date|time|counter|n)\}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Expand(string template, DateTime now, int counter = 1)
    {
        var name = TokenRegex.Replace(template, m => m.Groups[1].Value.ToLowerInvariant() switch
        {
            "date" => now.ToString("yyyy-MM-dd"),
            "time" => now.ToString("HH-mm-ss"),
            _ => counter.ToString(),
        });
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.');
        return name.Length == 0 ? "PIXORA" : name;
    }

    /// <summary>Returns a path in <paramref name="dir"/> that does not exist yet (appends " (2)" etc.).</summary>
    public static string UniquePath(string dir, string baseName, string extension)
    {
        if (!extension.StartsWith('.')) extension = "." + extension;
        var candidate = Path.Combine(dir, baseName + extension);
        for (int i = 2; File.Exists(candidate); i++)
            candidate = Path.Combine(dir, $"{baseName} ({i}){extension}");
        return candidate;
    }

    public static string Extension(ImageFormat f) => f switch
    {
        ImageFormat.Png => ".png",
        ImageFormat.Jpeg => ".jpg",
        ImageFormat.WebP => ".webp",
        _ => ".png",
    };

    public static bool TryFormatFromExtension(string path, out ImageFormat format)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".png": format = ImageFormat.Png; return true;
            case ".jpg" or ".jpeg": format = ImageFormat.Jpeg; return true;
            case ".webp": format = ImageFormat.WebP; return true;
            default: format = ImageFormat.Png; return false;
        }
    }
}
