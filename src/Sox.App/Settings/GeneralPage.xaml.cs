using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sox.App.Services;
using Sox.Core;

namespace Sox.App.Settings;

public sealed partial class GeneralPage : Page
{
    private UserSettings _settings = UserSettings.Load();
    private bool _loading = true;

    public GeneralPage()
    {
        InitializeComponent();

        // The registry is the source of truth for startup, not the setting: a user who disabled the
        // Run value outside Sox should see it reflected here.
        StartupToggle.IsOn = StartupService.IsEnabled();
        MinimizeToggle.IsOn = _settings.MinimizeToTrayOnStart;
        AutoUpdateToggle.IsOn = _settings.AutoCheckUpdates;
        TrayToggle.IsOn = !_settings.HideTrayIcon;
        _loading = false;
    }

    private void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        StartupService.SetEnabled(StartupToggle.IsOn);
        Save();
    }

    private void OnMinimizeToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Save();
    }

    private void OnAutoUpdateToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Save();
    }

    private void OnTrayToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Save();
    }

    private void Save()
    {
        SyncLatest();
        _settings.Save();
        (Microsoft.UI.Xaml.Application.Current as App)?.RaiseSettingsChanged();
    }

    // Another process or page may have replaced the cached instance since this page was built. Adopt the
    // live one and re-apply this page's controls before writing, so saving here never reverts their work.
    private void SyncLatest()
    {
        _settings = UserSettings.Load();
        _settings.StartWithWindows = StartupToggle.IsOn;
        _settings.MinimizeToTrayOnStart = MinimizeToggle.IsOn;
        _settings.AutoCheckUpdates = AutoUpdateToggle.IsOn;
        _settings.HideTrayIcon = !TrayToggle.IsOn;
    }
}
