namespace PIXORA.Core;

/// <summary>Minimal file logger. Never logs captured content, only operational events.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _file;

    public static void Init(AppPaths paths)
    {
        Directory.CreateDirectory(paths.LogDir);
        _file = Path.Combine(paths.LogDir, $"pixora-{DateTime.Now:yyyy-MM-dd}.log");
        try
        {
            foreach (var old in Directory.EnumerateFiles(paths.LogDir, "pixora-*.log"))
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(old) > TimeSpan.FromDays(14)) File.Delete(old);
        }
        catch (IOException) { }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");

    private static void Write(string level, string message)
    {
        var f = _file;
        if (f == null) return;
        try
        {
            lock (Gate) File.AppendAllText(f, $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
