using Microsoft.UI.Xaml;
using Sox.App.Materials;

namespace Sox.App.Services;

internal sealed record ThemeContext
{
    public ElementTheme Theme { get; init; }

    public BackdropStyle BackdropStyle { get; init; } = BackdropStyle.Acrylic;
}
