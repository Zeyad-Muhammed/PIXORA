using Microsoft.Win32;
using PIXORA.Core;

namespace PIXORA.App.Services;

/// <summary>Per-user "start with Windows" entry (HKCU Run key). No admin rights needed.</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PIXORA Capture Studio";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (enabled)
                key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --tray");
            else if (key.GetValue(ValueName) != null)
                key.DeleteValue(ValueName, false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Warn($"Could not update startup entry: {ex.Message}");
        }
    }

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) != null;
    }
}
