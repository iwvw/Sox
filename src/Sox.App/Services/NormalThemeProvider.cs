using Microsoft.UI.Xaml;
using Sox.App.Materials;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Sox.App.Services;

internal sealed class NormalThemeProvider : IThemeProvider
{
    private static readonly Color DarkBaseColor = Color.FromArgb(255, 32, 32, 32);
    private static readonly Color LightBaseColor = Color.FromArgb(255, 243, 243, 243);
    private readonly UISettings _uiSettings;

    public NormalThemeProvider(UISettings uiSettings)
    {
        ArgumentNullException.ThrowIfNull(uiSettings);
        _uiSettings = uiSettings;
    }

    public string ThemeKey => "normal";

    public BackdropParameters GetBackdropParameters(ThemeContext context)
    {
        var isLight = context.Theme == ElementTheme.Light ||
                      (context.Theme == ElementTheme.Default &&
                       _uiSettings.GetColorValue(UIColorType.Background).R > 128);

        var baseLuminosityOpacity = isLight ? 0.9f : 0.96f;

        return new BackdropParameters(
            TintColor: isLight ? LightBaseColor : DarkBaseColor,
            FallbackColor: isLight ? LightBaseColor : DarkBaseColor,
            EffectiveOpacity: 0.5f,
            EffectiveLuminosityOpacity: baseLuminosityOpacity * 0.5f,
            Style: context.BackdropStyle);
    }
}
