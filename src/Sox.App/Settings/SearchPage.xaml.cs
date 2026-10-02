using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sox.Core;
namespace Sox.App.Settings;

public sealed partial class SearchPage : Page
{
    private UserSettings _settings;
    private bool _loading = true;

    public SearchPage()
    {
        _settings = UserSettings.Load();
        InitializeComponent();

        foreach (var item in MaxResultsCombo.Items)
        {
            if (item is ComboBoxItem cbi &&
                int.TryParse((string)cbi.Content, out var value) &&
                value == _settings.MaxResults)
            {
                MaxResultsCombo.SelectedItem = cbi;
                break;
            }
        }

        FuzzyToggle.IsOn = _settings.EnableFuzzyMatch;
        OrFirstToggle.IsOn = _settings.OrFirstPrecedence;
        HistoryToggle.IsOn = _settings.EnableHistory;
        KeywordHistoryToggle.IsOn = _settings.EnableKeywordHistory;
        AsciiOnlyToggle.IsOn = _settings.AsciiOnlySearchBox;
        ExcludedPathsBox.Text = string.Join(Environment.NewLine, _settings.ExcludedPaths);
        IgnoredGlobsBox.Text = string.Join(Environment.NewLine, _settings.IgnoredPathGlobs);
        IgnoredRegexBox.Text = string.Join(Environment.NewLine, _settings.IgnoredPathRegexes);
        _loading = false;
    }

    private int MaxResultsValue =>
        MaxResultsCombo.SelectedItem is ComboBoxItem cbi && int.TryParse((string)cbi.Content, out var v)
            ? v
            : _settings.MaxResults;

    private void OnFuzzyToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.EnableFuzzyMatch = FuzzyToggle.IsOn;
        SearchContext.DefaultFuzzyMatchEnabled = FuzzyToggle.IsOn;
        Save();
    }

    private void OnOrFirstToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.OrFirstPrecedence = OrFirstToggle.IsOn;
        SearchContext.DefaultAndFirstPrecedence = !OrFirstToggle.IsOn;
        Save();
    }

    private void OnHistoryToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.EnableHistory = HistoryToggle.IsOn;
        Save();
    }

    private void OnKeywordHistoryToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.EnableKeywordHistory = KeywordHistoryToggle.IsOn;
        Save();
    }

    private void OnAsciiOnlyToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.AsciiOnlySearchBox = AsciiOnlyToggle.IsOn;
        Save();
    }

    private void OnMaxResultsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _settings.MaxResults = MaxResultsValue;
        Save();
    }

    private void Save()
    {
        _settings.ExcludedPaths = SplitLines(ExcludedPathsBox.Text);
        _settings.IgnoredPathGlobs = SplitLines(IgnoredGlobsBox.Text);
        _settings.IgnoredPathRegexes = SplitLines(IgnoredRegexBox.Text);
        _settings.Save();
        RaiseSettingsChanged();
    }

    private static void RaiseSettingsChanged() =>
        (Microsoft.UI.Xaml.Application.Current as App)?.RaiseSettingsChanged();

    private static List<string> SplitLines(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}