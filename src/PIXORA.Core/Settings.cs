using System.Text.Json;
using System.Text.Json.Serialization;

namespace PIXORA.Core;

public sealed class HotkeySettings
{
    public string RegionCapture { get; set; } = "Ctrl+Shift+S";
    public string FullScreenCapture { get; set; } = "Ctrl+Shift+F";
    public string WindowCapture { get; set; } = "Ctrl+Shift+W";
    public string ToggleRecording { get; set; } = "Ctrl+Shift+R";
    public string OpenEditor { get; set; } = "Ctrl+Shift+E";
}

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    // General
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool ShowTrayIcon { get; set; } = true;
    public bool ShowNotifications { get; set; } = true;
    public AppTheme Theme { get; set; } = AppTheme.Dark;

    // Locations and naming
    public string ScreenshotDir { get; set; } = AppPaths.DefaultPicturesDir();
    public string RecordingDir { get; set; } = AppPaths.DefaultVideosDir();
    public string ScreenshotNameTemplate { get; set; } = "Screenshot_{date}_{time}";
    public string RecordingNameTemplate { get; set; } = "Recording_{date}_{time}";

    // Screenshots
    public ImageFormat ScreenshotFormat { get; set; } = ImageFormat.Png;
    public int ImageQuality { get; set; } = 90;
    public int CaptureDelaySeconds { get; set; }
    public bool CopyToClipboardAfterCapture { get; set; }
    public bool AutoSaveScreenshots { get; set; } = true;
    public bool OpenEditorAfterCapture { get; set; } = true;

    // Recording
    public int RecordingFrameRate { get; set; } = 30;
    public int RecordingHeight { get; set; }          // 0 = native
    public VideoQuality RecordingQuality { get; set; } = VideoQuality.High;
    public int RecordingCountdownSeconds { get; set; } = 3;
    public bool RecordSystemAudio { get; set; } = true;
    public bool RecordMicrophone { get; set; }
    public string? MicrophoneDeviceId { get; set; }
    public double SystemAudioVolume { get; set; } = 1.0;
    public double MicrophoneVolume { get; set; } = 1.0;
    public bool CaptureCursor { get; set; } = true;

    public HotkeySettings Hotkeys { get; set; } = new();
}

public static class SettingsValidator
{
    public static readonly int[] FrameRates = { 15, 24, 30, 60 };

    /// <summary>Returns human-readable problems; empty when the settings are valid.</summary>
    public static IReadOnlyList<string> Validate(AppSettings s)
    {
        var errors = new List<string>();
        CheckDir(s.ScreenshotDir, "Screenshot folder", errors);
        CheckDir(s.RecordingDir, "Recording folder", errors);
        CheckTemplate(s.ScreenshotNameTemplate, "Screenshot name template", errors);
        CheckTemplate(s.RecordingNameTemplate, "Recording name template", errors);
        if (s.ImageQuality is < 1 or > 100) errors.Add("Image quality must be between 1 and 100.");
        if (s.CaptureDelaySeconds is < 0 or > 30) errors.Add("Capture delay must be between 0 and 30 seconds.");
        if (!FrameRates.Contains(s.RecordingFrameRate)) errors.Add("Frame rate must be 15, 24, 30 or 60.");
        if (s.RecordingHeight is not (0 or 480 or 720 or 1080 or 1440 or 2160)) errors.Add("Recording resolution is not supported.");
        if (s.RecordingCountdownSeconds is < 0 or > 10) errors.Add("Countdown must be between 0 and 10 seconds.");
        if (s.SystemAudioVolume is < 0 or > 2) errors.Add("System audio volume must be between 0 and 200%.");
        if (s.MicrophoneVolume is < 0 or > 2) errors.Add("Microphone volume must be between 0 and 200%.");
        var hk = new[]
        {
            s.Hotkeys.RegionCapture, s.Hotkeys.FullScreenCapture, s.Hotkeys.WindowCapture,
            s.Hotkeys.ToggleRecording, s.Hotkeys.OpenEditor
        };
        foreach (var h in hk)
            if (!HotkeyParser.TryParse(h, out _, out _)) errors.Add($"Shortcut \"{h}\" is not valid. Use e.g. Ctrl+Shift+S.");
        var assigned = hk.Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Replace(" ", "").ToUpperInvariant()).ToList();
        if (assigned.Distinct().Count() != assigned.Count) errors.Add("Each shortcut must be unique.");
        return errors;
    }

    private static void CheckDir(string? path, string label, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(path)) { errors.Add($"{label} is required."); return; }
        try
        {
            if (!Path.IsPathRooted(path)) errors.Add($"{label} must be an absolute path.");
            if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) errors.Add($"{label} contains invalid characters.");
            _ = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            errors.Add($"{label} is not a valid path.");
        }
    }

    private static void CheckTemplate(string? t, string label, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(t)) { errors.Add($"{label} is required."); return; }
        var stripped = NamingTemplate.TokenRegex.Replace(t, "");
        if (stripped.Contains('{') || stripped.Contains('}'))
        {
            errors.Add($"{label} has an unknown token. Use {{date}}, {{time}} or {{counter}}.");
            return;
        }
        if (stripped.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) errors.Add($"{label} contains characters not allowed in file names.");
    }

    /// <summary>Creates the directory to prove it is writable. Returns an error message or null.</summary>
    public static string? ProbeWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".pixora-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return $"Cannot write to \"{dir}\": {ex.Message}";
        }
    }
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    private readonly AppPaths _paths;
    private readonly object _gate = new();

    public SettingsStore(AppPaths paths) => _paths = paths;

    public AppSettings Current { get; private set; } = new();

    public event Action<AppSettings>? Changed;

    /// <summary>Loads settings; a missing or corrupt file yields defaults (the corrupt file is kept as .bad).</summary>
    public AppSettings Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_paths.SettingsFile))
                {
                    var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_paths.SettingsFile), Json);
                    if (loaded != null) { Current = Normalize(loaded); return Current; }
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                try { File.Move(_paths.SettingsFile, _paths.SettingsFile + ".bad", true); } catch (IOException) { }
            }
            Current = new AppSettings();
            return Current;
        }
    }

    /// <summary>Validates then atomically persists. Throws <see cref="SettingsException"/> when invalid.</summary>
    public void Save(AppSettings settings)
    {
        var errors = SettingsValidator.Validate(settings);
        if (errors.Count > 0) throw new SettingsException(errors);
        lock (_gate)
        {
            Directory.CreateDirectory(_paths.SettingsDir);
            var tmp = _paths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
            File.Move(tmp, _paths.SettingsFile, true);
            Current = settings;
        }
        Changed?.Invoke(settings);
    }

    public AppSettings RestoreDefaults()
    {
        var d = new AppSettings();
        Save(d);
        return d;
    }

    /// <summary>Deep copy so the UI can edit without mutating the live settings.</summary>
    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(Current, Json), Json)!;

    private static AppSettings Normalize(AppSettings s)
    {
        s.Hotkeys ??= new HotkeySettings();
        if (string.IsNullOrWhiteSpace(s.ScreenshotDir)) s.ScreenshotDir = AppPaths.DefaultPicturesDir();
        if (string.IsNullOrWhiteSpace(s.RecordingDir)) s.RecordingDir = AppPaths.DefaultVideosDir();
        return s;
    }
}

public sealed class SettingsException : Exception
{
    public IReadOnlyList<string> Errors { get; }
    public SettingsException(IReadOnlyList<string> errors) : base(string.Join(" ", errors)) => Errors = errors;
}
