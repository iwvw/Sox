using Sox.App.Materials;

namespace Sox.App.Services;

internal interface IThemeProvider
{
    string ThemeKey { get; }

    BackdropParameters GetBackdropParameters(ThemeContext context);
}
