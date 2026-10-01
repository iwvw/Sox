using Windows.UI;

namespace Sox.App.Materials;

public sealed record BackdropParameters(
    Color TintColor,
    Color FallbackColor,
    float EffectiveOpacity,
    float EffectiveLuminosityOpacity,
    BackdropStyle Style = BackdropStyle.Acrylic);
