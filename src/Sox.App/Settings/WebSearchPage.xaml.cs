using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Sox.Core;

namespace Sox.App.Settings;

public sealed partial class WebSearchPage : Page
{
    private readonly UserSettings _settings;
    private readonly ObservableCollection<EngineRow> _rows = new();

    public WebSearchPage()
    {
        _settings = UserSettings.Load();
        InitializeComponent();

        foreach (var engine in _settings.WebSearchEngines)
            _rows.Add(new EngineRow(engine));

        EngineList.ItemsSource = _rows;
    }

    private void OnRowToggled(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: EngineRow row })
        {
            row.Model.Enabled = row.Enabled;
            Save();
        }
    }

    private async void OnAddEngine(object sender, RoutedEventArgs e)
    {
        var draft = new WebSearchEngineSetting();
        if (await EditEngineAsync(draft, isNew: true))
        {
            _settings.WebSearchEngines.Add(draft);
            _rows.Add(new EngineRow(draft));
            Save();
        }
    }

    private async void OnEditEngine(object sender, RoutedEventArgs e)
    {
        if (EngineList.SelectedItem is not EngineRow row)
            return;

        if (!await EditEngineAsync(row.Model, isNew: false))
            return;

        row.Refresh();
        Save();
    }

    private void OnDeleteEngine(object sender, RoutedEventArgs e)
    {
        if (EngineList.SelectedItem is not EngineRow row)
            return;

        _settings.WebSearchEngines.Remove(row.Model);
        _rows.Remove(row);
        Save();
    }

    private async Task<bool> EditEngineAsync(WebSearchEngineSetting engine, bool isNew)
    {
        var keyword = new TextBox { Header = "关键字", Text = engine.Keyword };
        var name = new TextBox { Header = "标题", Text = engine.Name };
        var url = new TextBox { Header = "搜索地址（用 %s 代表关键词）", Text = engine.UrlTemplate };
        var suggest = new TextBox { Header = "建议接口（可选，用 %s 代表关键词）", Text = engine.SuggestUrl };
        var icon = new TextBox { Header = "图标文件路径（可选，留空用内置图标）", Text = engine.IconPath };
        var glyph = new TextBox { Header = "内置图标字形（可选）", Text = engine.Glyph };

        var panel = new StackPanel { Spacing = 10, Width = 460 };
        panel.Children.Add(keyword);
        panel.Children.Add(name);
        panel.Children.Add(url);
        panel.Children.Add(suggest);
        panel.Children.Add(icon);
        panel.Children.Add(glyph);

        var dialog = new ContentDialog
        {
            Title = isNew ? "添加搜索引擎" : "编辑搜索引擎",
            Content = new ScrollViewer { Content = panel, MaxHeight = 460 },
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return false;

        if (string.IsNullOrWhiteSpace(keyword.Text) || string.IsNullOrWhiteSpace(url.Text))
        {
            var warn = new ContentDialog
            {
                Title = "无法保存",
                Content = "关键字与搜索地址不能为空。",
                CloseButtonText = "确定",
                XamlRoot = XamlRoot,
            };
            await warn.ShowAsync();
            return false;
        }

        engine.Keyword = keyword.Text.Trim();
        engine.Name = string.IsNullOrWhiteSpace(name.Text) ? engine.Keyword : name.Text.Trim();
        engine.UrlTemplate = url.Text.Trim();
        engine.SuggestUrl = suggest.Text.Trim();
        engine.IconPath = icon.Text.Trim();
        engine.Glyph = string.IsNullOrWhiteSpace(glyph.Text) ? "\uE721" : glyph.Text.Trim();
        return true;
    }

    private void Save()
    {
        _settings.Save();
        (Microsoft.UI.Xaml.Application.Current as App)?.RaiseSettingsChanged();
    }

    /// <summary>Row view-model over a stored engine so the list binds and refreshes cleanly.</summary>
    public sealed class EngineRow : INotifyPropertyChanged
    {
        public EngineRow(WebSearchEngineSetting model) => Model = model;

        public WebSearchEngineSetting Model { get; }

        public bool Enabled
        {
            get => Model.Enabled;
            set => Model.Enabled = value;
        }

        public string Keyword => Model.Keyword;
        public string Name => Model.Name;
        public string IconLabel => string.IsNullOrWhiteSpace(Model.IconPath) ? "内置" : "自定义";

        public event PropertyChangedEventHandler? PropertyChanged;

        public void Refresh()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Keyword)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconLabel)));
        }
    }
}
