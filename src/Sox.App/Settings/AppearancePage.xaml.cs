using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sox.App.Materials;
using Sox.App.Services;
using Sox.Core;

namespace Sox.App.Settings;

public sealed partial class AppearancePage : Page
{
    private readonly ThemeService _themeService;
    private UserSettings _settings;
    private bool _loading = true;

    public AppearancePage(ThemeService themeService)
    {
        _themeService = themeService;
        _settings = UserSettings.Load();
        InitializeComponent();
        Populate();
        _loading = false;
    }

    /// <summary>Called when a setting changes; persists material + theme the spotlight window reads.</summary>
    public event Action? SettingsChanged;

    private void Populate()
    {
        ThemeFollowSystemToggle.IsOn = _settings.ThemeFollowSystem;
        DarkThemeToggle.IsOn = string.Equals(_settings.Theme, "Dark", StringComparison.OrdinalIgnoreCase);
        DarkThemeToggle.IsEnabled = !_settings.ThemeFollowSystem;

        BackdropCombo.SelectedIndex = (int)_themeService.BackdropStyle;

        SearchBarWidthBox.Text = _settings.SearchWindow.SearchBarWidth.ToString();
        PreviewWidthBox.Text = _settings.PreviewWindow.Width.ToString();
    }

    private void OnThemeFollowSystemToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.ThemeFollowSystem = ThemeFollowSystemToggle.IsOn;
        DarkThemeToggle.IsEnabled = !ThemeFollowSystemToggle.IsOn;
        Save();
    }

    private void OnDarkThemeToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Theme = DarkThemeToggle.IsOn ? "Dark" : "Light";
        Save();
    }

    private void OnBackdropChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || BackdropCombo.SelectedIndex < 0) return;

        // Apply and persist the choice directly. Reload() would re-read the file and overwrite the
        // value we just set, so save first, then reload from the (now correct) file.
        _themeService.BackdropStyle = (BackdropStyle)BackdropCombo.SelectedIndex;
        _themeService.SaveMaterial();
        _themeService.Reload();
        _settings = UserSettings.Load();
        SettingsChanged?.Invoke();
        RaiseSettingsChanged();
    }

    private void OnTextChangedCommit(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        Save();
    }

    private void Save()
    {
        if (int.TryParse(SearchBarWidthBox.Text, out var w))
        {
            _settings.SearchWindow.SearchBarWidth = w;
        }

        if (int.TryParse(PreviewWidthBox.Text, out var pw))
        {
            _settings.PreviewWindow.Width = pw;
        }

        _settings.Save();
        _themeService.Reload();
        SettingsChanged?.Invoke();
        RaiseSettingsChanged();
    }

    private static void RaiseSettingsChanged() =>
        (Microsoft.UI.Xaml.Application.Current as App)?.RaiseSettingsChanged();
}