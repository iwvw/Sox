using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Sox.App.Services;
using Sox.Core;
using Sox.Core.Indexer.Usn;

namespace Sox.App.Settings;

public sealed partial class IndexPage : Page
{
    private readonly SearchHost _searchHost;
    private CancellationTokenSource? _statusCts;
    private MachineSettings? _machineSettings;
    private readonly UserSettings _settings = UserSettings.Load();
    private readonly ObservableCollection<PriorityRow> _priorityRows = new();

    public IndexPage(SearchHost searchHost)
    {
        _searchHost = searchHost;
        InitializeComponent();

        foreach (var rule in _settings.PathPriorities)
            _priorityRows.Add(new PriorityRow(rule));
        PriorityList.ItemsSource = _priorityRows;

        _ = LoadIndexStateAsync();
    }

    private async Task LoadIndexStateAsync()
    {
        try
        {
            _machineSettings = await _searchHost.GetMachineSettingsAsync();

            // MachineSettings.LocalDrives holds opaque volume cache keys (a hash of the NTFS serial),
            // not drive letters, so showing them directly put a 64-char hex string in the dropdown.
            // Map each configured key back to its drive letter for display, and keep the key as the
            // item's Tag so rebuild/cancel still address the right volume.
            var configured = _machineSettings?.LocalDrives ?? new List<string>();
            var configuredSet = new HashSet<string>(configured, StringComparer.OrdinalIgnoreCase);

            DriveCombo.Items.Clear();
            foreach (var letter in Sox.Core.VolumeHelper.DetectIndexableLocalDrives())
            {
                var volumeId = Sox.Core.VolumeHelper.GetVolumeId(letter);
                if (volumeId is null || !configuredSet.Contains(volumeId))
                {
                    continue;
                }

                DriveCombo.Items.Add(new ComboBoxItem
                {
                    Content = $"{letter}:",
                    Tag = volumeId,
                });
            }

            if (DriveCombo.Items.Count > 0)
            {
                DriveCombo.SelectedIndex = 0;
            }
        }
        catch (Exception ex)
        {
            Log.Error("LoadIndexState failed", ex);
        }

        var cts = new CancellationTokenSource();
        _statusCts = cts;
        _ = Task.Run(() => _searchHost.SubscribeStatusAsync(OnStatus, cts.Token));
    }

    private string? SelectedDriveId => (DriveCombo.SelectedItem as ComboBoxItem)?.Tag as string;

    private void OnStatus(UsnIndexer.IndexerStatus status)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            IndexStateText.Text = $"{status.State} {status.Progress}% · {status.TotalFiles:N0} 文件 / {status.TotalDirs:N0} 目录";
        });
    }

    private async void OnRebuildIndex(object sender, RoutedEventArgs e)
    {
        if (SelectedDriveId is not { } drive) return;
        RebuildButton.IsEnabled = false;
        try
        {
            var ok = await _searchHost.RebuildDriveIndexAsync(drive);
            Log.Info($"Rebuild '{drive}' -> {ok}");
        }
        finally
        {
            RebuildButton.IsEnabled = true;
        }
    }

    private async void OnCancelIndex(object sender, RoutedEventArgs e)
    {
        if (SelectedDriveId is not { } drive) return;
        CancelIndexButton.IsEnabled = false;
        try
        {
            var ok = await _searchHost.CancelDriveIndexAsync(drive);
            Log.Info($"Cancel index '{drive}' -> {ok}");
        }
        finally
        {
            CancelIndexButton.IsEnabled = true;
        }
    }

    private async void OnClearLog(object sender, RoutedEventArgs e)
    {
        try
        {
            var ok = await _searchHost.ClearServiceLogAsync();
            Log.Info($"Clear service log -> {ok}");
        }
        catch (Exception ex)
        {
            Log.Error("ClearServiceLog failed", ex);
        }
    }

    public void Cleanup()
    {
        _statusCts?.Cancel();
        _statusCts?.Dispose();
    }

    // ---- Priority rules ----

    private async void OnAddPriority(object sender, RoutedEventArgs e)
    {
        var draft = new PathPriorityRuleSetting { Priority = PathPriority.Normal };
        if (await EditPriorityAsync(draft, isNew: true))
        {
            _settings.PathPriorities.Add(draft);
            _priorityRows.Add(new PriorityRow(draft));
            SavePriorities();
        }
    }

    private async void OnEditPriority(object sender, RoutedEventArgs e)
    {
        if (PriorityList.SelectedItem is not PriorityRow row)
            return;

        await EditPriorityAsync(row.Model, isNew: false);
        row.Refresh();
        SavePriorities();
    }

    private void OnDeletePriority(object sender, RoutedEventArgs e)
    {
        if (PriorityList.SelectedItem is not PriorityRow row)
            return;

        _settings.PathPriorities.Remove(row.Model);
        _priorityRows.Remove(row);
        SavePriorities();
    }

    private async Task<bool> EditPriorityAsync(PathPriorityRuleSetting rule, bool isNew)
    {
        var path = new TextBox { Header = "目录", Text = rule.Path };
        var pick = new Button { Content = "浏览…", Margin = new Thickness(8, 0, 0, 0) };
        pick.Click += async (_, _) =>
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Current.MainWindow));
            picker.FileTypeFilter.Add("*");
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
                path.Text = folder.Path;
        };
        var pathRow = new StackPanel { Orientation = Orientation.Horizontal };
        pathRow.Children.Add(path);
        pathRow.Children.Add(pick);

        var combo = new ComboBox { Header = "优先级", MinWidth = 200 };
        combo.Items.Add("高（优先显示）");
        combo.Items.Add("正常");
        combo.Items.Add("不常用（靠后）");
        combo.Items.Add("不索引（排除）");
        combo.SelectedIndex = rule.Priority switch
        {
            PathPriority.High => 0,
            PathPriority.Uncommon => 2,
            PathPriority.Excluded => 3,
            _ => 1,
        };

        var panel = new StackPanel { Spacing = 12, Width = 460 };
        panel.Children.Add(pathRow);
        panel.Children.Add(combo);

        var dialog = new ContentDialog
        {
            Title = isNew ? "添加优先级规则" : "编辑优先级规则",
            Content = panel,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return false;

        if (string.IsNullOrWhiteSpace(path.Text))
            return false;

        rule.Path = path.Text.Trim();
        rule.Priority = combo.SelectedIndex switch
        {
            0 => PathPriority.High,
            2 => PathPriority.Uncommon,
            3 => PathPriority.Excluded,
            _ => PathPriority.Normal,
        };
        return true;
    }

    private void SavePriorities()
    {
        _settings.Save();
        (Microsoft.UI.Xaml.Application.Current as App)?.RaiseSettingsChanged();
    }

    /// <summary>Row view-model over a stored priority rule.</summary>
    public sealed class PriorityRow : INotifyPropertyChanged
    {
        public PriorityRow(PathPriorityRuleSetting model) => Model = model;

        public PathPriorityRuleSetting Model { get; }

        public string Path => Model.Path;

        public string PriorityLabel => Model.Priority switch
        {
            PathPriority.High => "高",
            PathPriority.Uncommon => "不常用",
            PathPriority.Excluded => "不索引",
            _ => "正常",
        };

        public event PropertyChangedEventHandler? PropertyChanged;

        public void Refresh()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Path)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PriorityLabel)));
        }
    }
}