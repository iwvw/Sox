using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sox.App.Services;
using Sox.Core;

namespace Sox.App.Settings;

public sealed partial class GeneralPage : Page
{
    private readonly UserSettings _settings;
    private bool _loading = true;

    public GeneralPage()
    {
        _settings = UserSettings.Load();
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
        _settings.StartWithWindows = StartupToggle.IsOn;
        StartupService.SetEnabled(StartupToggle.IsOn);
        Save();
    }

    private void OnMinimizeToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.MinimizeToTrayOnStart = MinimizeToggle.IsOn;
        Save();
    }

    private void OnAutoUpdateToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.AutoCheckUpdates = AutoUpdateToggle.IsOn;
        Save();
    }

    private void OnTrayToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.HideTrayIcon = !TrayToggle.IsOn;
        Save();
    }

    private void Save()
    {
        _settings.Save();
        (Microsoft.UI.Xaml.Application.Current as App)?.RaiseSettingsChanged();
    }
}
