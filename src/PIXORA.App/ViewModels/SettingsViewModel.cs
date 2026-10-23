using CommunityToolkit.Mvvm.ComponentModel;
using PIXORA.Core;

namespace PIXORA.App.ViewModels;

/// <summary>Edits a copy of the settings; nothing is applied until <see cref="Save"/> validates it.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _store;

    public SettingsViewModel(SettingsStore store)
    {
        _store = store;
        Draft = store.Clone();
    }

    public AppSettings Draft { get; private set; }

    [ObservableProperty] private string _errors = "";

    public void Reload() => Draft = _store.Clone();

    /// <summary>Validates (including folder writability) and persists. Returns false and fills <see cref="Errors"/> on failure.</summary>
    public bool Save()
    {
        var problems = SettingsValidator.Validate(Draft).ToList();
        foreach (var dir in new[] { Draft.ScreenshotDir, Draft.RecordingDir }.Distinct())
            if (problems.Count == 0 && SettingsValidator.ProbeWritable(dir) is { } p) problems.Add(p);
        if (problems.Count > 0) { Errors = string.Join("\n", problems); return false; }
        _store.Save(Draft);
        Draft = _store.Clone();
        Errors = "";
        return true;
    }

    public void RestoreDefaults()
    {
        _store.RestoreDefaults();
        Draft = _store.Clone();
        Errors = "";
    }

    public static string PreviewName(string template, string ext)
    {
        try { return NamingTemplate.Expand(template, DateTime.Now) + ext; }
        catch (ArgumentException) { return "(invalid)"; }
    }
}
