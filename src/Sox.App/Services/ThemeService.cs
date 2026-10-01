using Microsoft.UI.Xaml;
using Sox.App.Materials;
using Sox.Core;
using Windows.UI.ViewManagement;

namespace Sox.App.Services;

public sealed class ThemeService : IDisposable
{
    private readonly UISettings _uiSettings;
    private readonly NormalThemeProvider _provider;
    private ThemeSnapshot _current;

    public event EventHandler? ThemeChanged;

    public ThemeSnapshot Current => _current;

    public ThemeService()
    {
        _uiSettings = new UISettings();
        _provider = new NormalThemeProvider(_uiSettings);
        _uiSettings.ColorValuesChanged += OnSystemColorValuesChanged;
        _current = Build();
    }

    public void Reload()
    {
        _current = Build();
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    public BackdropStyle BackdropStyle { get; set; } = BackdropStyle.Acrylic;

    private ThemeSnapshot Build()
    {
        UserSettings settings;
        try
        {
            settings = UserSettings.Load();
        }
        catch
        {
            settings = new UserSettings();
        }

        ElementTheme theme;
        if (!File.Exists(UserSettings.SettingsPath) || settings.ThemeFollowSystem)
        {
            theme = ElementTheme.Default;
        }
        else
        {
            theme = string.Equals(settings.Theme, "Dark", StringComparison.OrdinalIgnoreCase)
                ? ElementTheme.Dark
                : ElementTheme.Light;
        }

        BackdropStyle = ParseBackdropStyle(settings.BackdropStyle);

        var context = new ThemeContext
        {
            Theme = theme,
            BackdropStyle = BackdropStyle,
        };

        return new ThemeSnapshot
        {
            Theme = theme,
            BackdropParameters = _provider.GetBackdropParameters(context),
        };
    }

    private static BackdropStyle ParseBackdropStyle(string value) =>
        Enum.TryParse<BackdropStyle>(value, ignoreCase: true, out var style) ? style : BackdropStyle.Acrylic;

    /// <summary>Persists the current material choice back into the user settings file.</summary>
    public void SaveMaterial()
    {
        try
        {
            var settings = UserSettings.Load();
            settings.BackdropStyle = BackdropStyle.ToString();
            settings.Save();
        }
        catch (Exception ex)
        {
            Log.Error("SaveMaterial failed", ex);
        }
    }

    private void OnSystemColorValuesChanged(UISettings sender, object args)
    {
        if (_current.Theme == ElementTheme.Default)
        {
            Reload();
        }
    }

    public void Dispose()
    {
        _uiSettings.ColorValuesChanged -= OnSystemColorValuesChanged;
    }
}

public sealed record ThemeSnapshot
{
    public ElementTheme Theme { get; init; }

    public BackdropParameters BackdropParameters { get; init; } = new(
        Microsoft.UI.Colors.Black, Microsoft.UI.Colors.Black, 0.5f, 0.5f);
}