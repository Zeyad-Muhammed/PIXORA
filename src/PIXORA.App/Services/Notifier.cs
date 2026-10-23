using Microsoft.UI.Xaml.Controls;
using PIXORA.Core;

namespace PIXORA.App.Services;

/// <summary>Routes user notifications: in-app banner when PIXORA is in front, otherwise a tray balloon (if enabled).</summary>
public sealed class Notifier
{
    private readonly SettingsStore _settings;

    public Notifier(SettingsStore settings) => _settings = settings;

    public void Info(string title, string message) => Show(title, message, InfoBarSeverity.Success);
    public void Warning(string title, string message) => Show(title, message, InfoBarSeverity.Warning);
    public void Error(string title, string message) => Show(title, message, InfoBarSeverity.Error);

    private void Show(string title, string message, InfoBarSeverity severity)
    {
        var main = App.MainWindow;
        if (main != null && main.IsShown && main.IsForeground)
        {
            main.ShowBanner(title, message, severity);
            return;
        }
        bool important = severity == InfoBarSeverity.Error;
        if ((_settings.Current.ShowNotifications || important) && App.Shell?.TrayVisible == true)
            App.Shell.Notify(title, message, severity != InfoBarSeverity.Success);
        else
            main?.ShowBanner(title, message, severity); // seen next time the window is opened
    }
}
