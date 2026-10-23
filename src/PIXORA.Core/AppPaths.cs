namespace PIXORA.Core;

/// <summary>Resolves user-writable locations. Nothing is ever written next to the executable.</summary>
public sealed class AppPaths
{
    public const string AppFolder = "PIXORA";

    public string SettingsDir { get; }
    public string DataDir { get; }
    public string LogDir => Path.Combine(DataDir, "logs");
    public string ThumbDir => Path.Combine(DataDir, "thumbs");
    public string TempDir => Path.Combine(DataDir, "temp");
    public string SettingsFile => Path.Combine(SettingsDir, "settings.json");
    public string LibraryDb => Path.Combine(DataDir, "library.db");

    public AppPaths(string settingsDir, string dataDir)
    {
        SettingsDir = settingsDir;
        DataDir = dataDir;
    }

    public static AppPaths Default() => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolder),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolder));

    /// <summary>Isolated roots below <paramref name="root"/>, used by tests.</summary>
    public static AppPaths Under(string root) => new(Path.Combine(root, "roaming"), Path.Combine(root, "local"));

    public void EnsureCreated()
    {
        foreach (var d in new[] { SettingsDir, DataDir, LogDir, ThumbDir, TempDir })
            Directory.CreateDirectory(d);
    }

    public static string DefaultPicturesDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "PIXORA", "Screenshots");

    public static string DefaultVideosDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "PIXORA", "Recordings");

    /// <summary>Deletes temp files left behind by an interrupted session.</summary>
    public void CleanTemp(TimeSpan olderThan)
    {
        if (!Directory.Exists(TempDir)) return;
        foreach (var f in Directory.EnumerateFiles(TempDir))
        {
            try
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > olderThan) File.Delete(f);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
