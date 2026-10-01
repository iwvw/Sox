using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sox.App.Services;
using Sox.Core;
using Sox.Core.Indexer.Usn;

namespace Sox.App.Settings;

public sealed partial class IndexPage : Page
{
    private readonly SearchHost _searchHost;
    private CancellationTokenSource? _statusCts;
    private MachineSettings? _machineSettings;

    public IndexPage(SearchHost searchHost)
    {
        _searchHost = searchHost;
        InitializeComponent();
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
}