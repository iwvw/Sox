namespace Sox.App.Materials;

public static class BackdropStyles
{
    private static readonly Dictionary<BackdropStyle, BackdropStyleConfig> Configs = new()
    {
        [BackdropStyle.Acrylic] = new()
        {
            ControllerKind = BackdropControllerKind.Acrylic,
            BaseTintOpacity = 0.5f,
            BaseLuminosityOpacity = 0.9f,
            PreviewBrush = PreviewBrushKind.Acrylic,
        },
        [BackdropStyle.Mica] = new()
        {
            ControllerKind = BackdropControllerKind.Mica,
            BaseTintOpacity = 0.0f,
            BaseLuminosityOpacity = 1.0f,
            PreviewBrush = PreviewBrushKind.Solid,
            FixedOpacity = 0.96f,
            SupportsOpacity = false,
        },
    };

    public static BackdropStyleConfig Get(BackdropStyle style) =>
        Configs.TryGetValue(style, out var config) ? config : Configs[BackdropStyle.Acrylic];

    public static IEnumerable<BackdropStyle> All => Configs.Keys;
}
