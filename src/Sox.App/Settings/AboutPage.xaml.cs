using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sox.App.Services;

namespace Sox.App.Settings;

public sealed partial class AboutPage : Page
{
    private readonly AppUpdateService _updates;
    private readonly Action _exitForUpdate;
    private AppUpdateInfo? _info;

    public AboutPage(AppUpdateService updates, Action exitForUpdate)
    {
        _updates = updates;
        _exitForUpdate = exitForUpdate;
        InitializeComponent();

        CurrentVersionText.Text = $"版本 {_updates.CurrentVersion}";
        if (AppUpdateService.LastResult is { } cached)
            Apply(cached);
    }

    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        CheckButton.IsEnabled = false;
        UpdateStatusText.Text = "正在检查…";
        try
        {
            var info = await _updates.CheckAsync();
            Apply(info);
        }
        finally
        {
            CheckButton.IsEnabled = true;
        }
    }

    private void Apply(AppUpdateInfo info)
    {
        _info = info;

        if (info.Error is not null)
        {
            UpdateStatusText.Text = "检查失败：" + info.Error;
            UpdateButton.Visibility = Visibility.Collapsed;
            return;
        }

        if (info.HasUpdate)
        {
            UpdateStatusText.Text = $"发现新版本 {info.LatestVersion}";
            UpdateButton.Visibility = Visibility.Visible;
        }
        else
        {
            UpdateStatusText.Text = "已是最新版本";
            UpdateButton.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnApplyUpdate(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        CheckButton.IsEnabled = false;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateStatusText.Text = "正在下载…";

        try
        {
            var progress = new Progress<double>(p =>
            {
                UpdateProgress.Value = p * 100;
                UpdateStatusText.Text = $"正在下载… {p * 100:F0}%";
            });

            var info = await _updates.PrepareUpdateAsync(progress);
            if (info.Error is not null)
            {
                UpdateStatusText.Text = info.Error;
                UpdateProgress.Visibility = Visibility.Collapsed;
                UpdateButton.IsEnabled = true;
                CheckButton.IsEnabled = true;
                return;
            }

            UpdateStatusText.Text = "即将重启并完成更新…";

            var script = _updates.ConsumePendingScript();
            if (script is not null)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"\"{script}\"\"",
                    CreateNoWindow = true,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                });
            }

            _exitForUpdate();
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = "更新失败：" + ex.Message;
            UpdateProgress.Visibility = Visibility.Collapsed;
            UpdateButton.IsEnabled = true;
            CheckButton.IsEnabled = true;
        }
    }
}
