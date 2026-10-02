using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sox.App.Services;

namespace Sox.App.Settings;

public sealed partial class AboutPage : Page
{
    private readonly AppUpdateService _updates;
    private readonly Action _exitForUpdate;
    private AppUpdateInfo? _info;

    // Third-party projects Sox builds on. Kept here rather than loaded from a file so the list ships
    // with the binary and cannot drift from what was actually compiled in.
    private static readonly OssComponent[] OpenSourceComponents =
    [
        new("Lertaro", "MIT", "https://github.com/Lertaro/Lertaro"),
        new("Windows App SDK (WinUI 3)", "MIT", "https://github.com/microsoft/WindowsAppSDK"),
        new("CommunityToolkit.WinUI", "MIT", "https://github.com/CommunityToolkit/Windows"),
        new("WinUIEx", "MIT", "https://github.com/dotMorten/WinUIEx"),
        new("H.NotifyIcon.WinUI", "MIT", "https://github.com/HavenDV/H.NotifyIcon"),
        new("Microsoft.Graphics.Win2D", "MIT", "https://github.com/microsoft/Win2D"),
        new("System.ServiceProcess.ServiceController", "MIT", "https://github.com/dotnet/runtime"),
    ];

    public sealed record OssComponent(string Name, string License, string Url);

    public AboutPage(AppUpdateService updates, Action exitForUpdate)
    {
        _updates = updates;
        _exitForUpdate = exitForUpdate;
        InitializeComponent();

        CurrentVersionText.Text = $"版本 {_updates.CurrentVersion}";
        OssList.ItemsSource = OpenSourceComponents;

        if (AppUpdateService.LastResult is { } cached)
            Apply(cached);
    }

    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        CheckButton.IsEnabled = false;
        ShowStatus("正在检查…", secondary: true);
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
            ShowStatus("检查失败：" + info.Error, secondary: false);
            UpdateButton.Visibility = Visibility.Collapsed;
            return;
        }

        if (info.HasUpdate)
        {
            ShowStatus($"发现新版本 {info.LatestVersion}", secondary: false);
            UpdateButton.Visibility = Visibility.Visible;
        }
        else
        {
            ShowStatus("已是最新版本", secondary: true);
            UpdateButton.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowStatus(string text, bool secondary)
    {
        UpdateStatusText.Text = text;
        UpdateStatusText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
            secondary ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush"];
        UpdateStatusText.Visibility = Visibility.Visible;
    }

    private async void OnApplyUpdate(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        CheckButton.IsEnabled = false;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateProgress.Value = 0;
        ShowStatus("正在下载…", secondary: true);

        try
        {
            var progress = new Progress<double>(p =>
            {
                UpdateProgress.Value = p * 100;
                ShowStatus($"正在下载… {p * 100:F0}%", secondary: true);
            });

            var info = await _updates.PrepareUpdateAsync(progress);
            if (info.Error is not null)
            {
                ShowStatus(info.Error, secondary: false);
                UpdateProgress.Visibility = Visibility.Collapsed;
                UpdateButton.IsEnabled = true;
                CheckButton.IsEnabled = true;
                return;
            }

            ShowStatus("即将重启并完成更新…", secondary: true);

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
            ShowStatus("更新失败：" + ex.Message, secondary: false);
            UpdateProgress.Visibility = Visibility.Collapsed;
            UpdateButton.IsEnabled = true;
            CheckButton.IsEnabled = true;
        }
    }
}
