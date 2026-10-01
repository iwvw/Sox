namespace Sox.App.Materials;

public sealed record BackdropStyleConfig
{
    public required BackdropControllerKind ControllerKind { get; init; }

    public required float BaseTintOpacity { get; init; }

    public required float BaseLuminosityOpacity { get; init; }

    public required PreviewBrushKind PreviewBrush { get; init; }

    public float FixedOpacity { get; init; }

    public bool SupportsColorization { get; init; } = true;

    public bool SupportsBackgroundImage { get; init; } = true;

    public bool SupportsOpacity { get; init; } = true;

    public float ComputeEffectiveOpacity(float userOpacity, float? baseTintOpacityOverride = null)
    {
        if (!SupportsOpacity && FixedOpacity > 0)
        {
            return FixedOpacity;
        }

        if (ControllerKind == BackdropControllerKind.Solid)
        {
            return userOpacity;
        }

        var baseTint = baseTintOpacityOverride ?? BaseTintOpacity;
        return baseTint * userOpacity;
    }
}
