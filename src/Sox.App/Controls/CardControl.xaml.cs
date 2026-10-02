using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Sox.App.Materials;
using Sox.App.Services;
using Windows.UI;

namespace Sox.App.Controls;

public sealed partial class CardControl : UserControl, IDisposable
{
    private readonly TintedControllerBackdrop _backdrop = new();
    private readonly ThemeShadow _cardShadow = new();
    private Color _cardFallbackBackground;

    public static readonly DependencyProperty MainContentProperty =
        DependencyProperty.Register(
            nameof(MainContent),
            typeof(object),
            typeof(CardControl),
            new PropertyMetadata(null));

    public static readonly DependencyProperty ShadowPaddingProperty =
        DependencyProperty.Register(
            nameof(ShadowPadding),
            typeof(Thickness),
            typeof(CardControl),
            new PropertyMetadata(new Thickness(16)));

    public static readonly DependencyProperty CardCornerRadiusProperty =
        DependencyProperty.Register(
            nameof(CardCornerRadius),
            typeof(CornerRadius),
            typeof(CardControl),
            new PropertyMetadata(new CornerRadius(8)));

    public static readonly DependencyProperty ShowShadowProperty =
        DependencyProperty.Register(
            nameof(ShowShadow),
            typeof(bool),
            typeof(CardControl),
            new PropertyMetadata(true, OnShowShadowChanged));

    public object? MainContent
    {
        get => GetValue(MainContentProperty);
        set => SetValue(MainContentProperty, value);
    }

    public Thickness ShadowPadding
    {
        get => (Thickness)GetValue(ShadowPaddingProperty);
        set => SetValue(ShadowPaddingProperty, value);
    }

    public CornerRadius CardCornerRadius
    {
        get => (CornerRadius)GetValue(CardCornerRadiusProperty);
        set => SetValue(CardCornerRadiusProperty, value);
    }

    public bool ShowShadow
    {
        get => (bool)GetValue(ShowShadowProperty);
        set => SetValue(ShowShadowProperty, value);
    }

    private static void OnShowShadowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CardControl card)
        {
            card.CardBorder.Shadow = (bool)e.NewValue ? card._cardShadow : null;
        }
    }

    public FrameworkElement CardElement => CardBorder;

    public Panel CardContentPanel => CardContent;

    public bool IsBackdropAttached => _backdrop.IsBackdropAttached;

    public CardControl()
    {
        InitializeComponent();
        _backdrop.BackdropAttachmentChanged += OnBackdropAttachmentChanged;
        BackdropElement.SystemBackdrop = _backdrop;
        CardBorder.Shadow = _cardShadow;
    }

    public void SetCardMaxHeight(double maxHeightDip)
    {
        CardBorder.MaxHeight = maxHeightDip;
    }

    public double GetCardHeight()
    {
        CardBorder.UpdateLayout();
        return CardBorder.ActualHeight;
    }

    public void SetCardStretch(bool stretch)
    {
        CardBorder.VerticalAlignment = stretch ? VerticalAlignment.Stretch : VerticalAlignment.Top;
    }

    public void SetIsInputActive(bool isActive)
    {
        _backdrop.IsInputActive = isActive;
    }

    public void ClearBackdrop() => Dispose();

    public void Dispose() => _backdrop.Dispose();

    public void ApplyBackdrop(BackdropParameters backdrop, BackdropControllerKind kind, bool isImageMode, bool hasColorization)
    {
        try
        {
            _cardFallbackBackground = CreateCardBackground(backdrop, kind);
            SetCardBackground(_cardFallbackBackground);
            _backdrop.Update(backdrop, kind, isImageMode, hasColorization);
            UpdateCardBackground(_backdrop.IsBackdropAttached);
        }
        catch (Exception ex)
        {
            SetCardBackground(backdrop.FallbackColor);
            Log.Error("Failed to apply backdrop to CardControl", ex);
        }
    }

    private void SetCardBackground(Color color)
    {
        if (CardBorder.Background is SolidColorBrush background)
        {
            background.Color = color;
        }
        else
        {
            CardBorder.Background = new SolidColorBrush(color);
        }
    }

    private void OnBackdropAttachmentChanged(bool isBackdropAttached)
    {
        UpdateCardBackground(isBackdropAttached);
    }

    private void UpdateCardBackground(bool isBackdropAttached)
    {
        SetCardBackground(isBackdropAttached ? Colors.Transparent : _cardFallbackBackground);
    }

    private static Color CreateCardBackground(BackdropParameters backdrop, BackdropControllerKind kind)
    {
        if (kind == BackdropControllerKind.Solid)
        {
            return Color.FromArgb(
                (byte)(backdrop.EffectiveOpacity * 255),
                backdrop.TintColor.R,
                backdrop.TintColor.G,
                backdrop.TintColor.B);
        }

        return backdrop.FallbackColor;
    }
}
