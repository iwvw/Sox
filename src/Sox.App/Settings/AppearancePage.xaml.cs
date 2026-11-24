using Microsoft.UI.Dispatching;
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
    private DispatcherQueueTimer? _commitTimer;
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
        DarkThemeToggle.IsEnabled = !ThemeFollowSystemToggle.IsOn;
        Save();
    }

    private void OnDarkThemeToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
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

        // Typing "1200" used to mean four full settings writes (each rotating five backups and
        // rebuilding the theme). Wait for a pause; a commit on focus loss is not needed because the
        // debounce fires even if focus moves on.
        _commitTimer ??= DispatcherQueue.CreateTimer();
        _commitTimer.Interval = TimeSpan.FromMilliseconds(400);
        _commitTimer.IsRepeating = false;
        _commitTimer.Tick -= OnCommitTimerTick;
        _commitTimer.Tick += OnCommitTimerTick;
        _commitTimer.Start();
    }

    private void OnCommitTimerTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        Save();
    }

    private void Save()
    {
        _settings = UserSettings.Load();
        if (int.TryParse(SearchBarWidthBox.Text, out var w))
        {
            _settings.SearchWindow.SearchBarWidth = Math.Clamp(w, MinSearchBarWidth, MaxSearchBarWidth);
        }

        if (int.TryParse(PreviewWidthBox.Text, out var pw) && pw > 0)
        {
            _settings.PreviewWindow.Width = Math.Clamp(pw, MinPreviewWidth, MaxPreviewWidth);
        }

        _settings.ThemeFollowSystem = ThemeFollowSystemToggle.IsOn;
        _settings.Theme = DarkThemeToggle.IsOn ? "Dark" : "Light";
        _settings.Save();
        _themeService.Reload();
        SettingsChanged?.Invoke();
        RaiseSettingsChanged();
    }

    private const int MinSearchBarWidth = 320;
    private const int MaxSearchBarWidth = 1600;
    private const int MinPreviewWidth = 200;
    private const int MaxPreviewWidth = 1200;

    private static void RaiseSettingsChanged() =>
        (Microsoft.UI.Xaml.Application.Current as App)?.RaiseSettingsChanged();
}