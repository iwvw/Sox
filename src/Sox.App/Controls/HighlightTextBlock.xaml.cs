using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Sox.Core.SearchIndex;

namespace Sox.App.Controls;

public sealed partial class HighlightTextBlock : UserControl
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(
            nameof(Text),
            typeof(string),
            typeof(HighlightTextBlock),
            new PropertyMetadata(string.Empty, OnChanged));

    public static readonly DependencyProperty QueryProperty =
        DependencyProperty.Register(
            nameof(Query),
            typeof(string),
            typeof(HighlightTextBlock),
            new PropertyMetadata(string.Empty, OnChanged));

    public static readonly DependencyProperty NormalForegroundProperty =
        DependencyProperty.Register(
            nameof(NormalForeground),
            typeof(Brush),
            typeof(HighlightTextBlock),
            new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty EmphasizedProperty =
        DependencyProperty.Register(
            nameof(Emphasized),
            typeof(bool),
            typeof(HighlightTextBlock),
            new PropertyMetadata(false, OnEmphasisChanged));

    public static readonly DependencyProperty BaseFontSizeProperty =
        DependencyProperty.Register(
            nameof(BaseFontSize),
            typeof(double),
            typeof(HighlightTextBlock),
            new PropertyMetadata(14.0, OnBaseFontSizeChanged));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string Query
    {
        get => (string)GetValue(QueryProperty);
        set => SetValue(QueryProperty, value);
    }

    public Brush? NormalForeground
    {
        get => (Brush?)GetValue(NormalForegroundProperty);
        set => SetValue(NormalForegroundProperty, value);
    }

    /// <summary>Renders the name larger and semi-bold; used for rows with no subtitle (bare apps).</summary>
    public bool Emphasized
    {
        get => (bool)GetValue(EmphasizedProperty);
        set => SetValue(EmphasizedProperty, value);
    }

    /// <summary>Normal (non-emphasized) text size. The panel uses a smaller value than the spotlight's
    /// default so its compact rows fit the docked strip.</summary>
    public double BaseFontSize
    {
        get => (double)GetValue(BaseFontSizeProperty);
        set => SetValue(BaseFontSizeProperty, value);
    }

    private static void OnEmphasisChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (HighlightTextBlock)d;
        control.ApplyFont();
    }

    private static void OnBaseFontSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (HighlightTextBlock)d;
        control.ApplyFont();
    }

    private void ApplyFont()
    {
        Inner.FontSize = Emphasized ? BaseFontSize + 2 : BaseFontSize;
        Inner.FontWeight = Emphasized
            ? Microsoft.UI.Text.FontWeights.SemiBold
            : Microsoft.UI.Text.FontWeights.Normal;
    }

    public HighlightTextBlock()
    {
        InitializeComponent();
        Inner.Foreground = NormalForeground;
        ApplyFont();
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((HighlightTextBlock)d).Rebuild();

    private void Rebuild()
    {
        Inner.Inlines.Clear();
        Inner.Foreground = NormalForeground;

        var text = Text ?? string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var query = Query ?? string.Empty;
        bool[] mask;
        try
        {
            mask = string.IsNullOrEmpty(query) ? Array.Empty<bool>() : FuzzyQuery.Parse(query).HighlightMask(text);
        }
        catch
        {
            mask = Array.Empty<bool>();
        }

        if (mask.Length != text.Length || !mask.Any(m => m))
        {
            Inner.Inlines.Add(new Run { Text = text });
            return;
        }

        // Prefer the theme-aware highlight brush (a saturated tint that stays legible on the acrylic
        // card); fall back to the accent colour, then to the normal foreground.
        Brush highlight = Application.Current.Resources.TryGetValue("Sox.HighlightBrush", out var brush)
            && brush is Brush themed
            ? themed
            : Application.Current.Resources["SystemAccentColor"] is Windows.UI.Color accent
                ? new SolidColorBrush(accent)
                : NormalForeground!;

        var start = 0;
        var current = mask[0];
        for (var i = 1; i <= text.Length; i++)
        {
            if (i == text.Length || mask[i] != current)
            {
                var run = new Run { Text = text.Substring(start, i - start) };
                if (current)
                {
                    run.Foreground = highlight;
                }

                Inner.Inlines.Add(run);
                start = i;
                if (i < text.Length)
                {
                    current = mask[i];
                }
            }
        }
    }
}
