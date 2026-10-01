using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Sox.App.Services;
using Sox.Core;
using Windows.System;
using Windows.UI.Core;

namespace Sox.App.Settings;

public sealed partial class HotkeyPage : Page
{
    private readonly UserSettings _settings;

    public HotkeyPage()
    {
        _settings = UserSettings.Load();
        InitializeComponent();
        SummonHotkeyBox.Text = _settings.SummonHotkey;
    }

    private void OnSummonHotkeyKeyDown(object sender, KeyRoutedEventArgs e)
    {
        e.Handled = true;

        var key = e.Key;
        if (key is VirtualKey.Control or VirtualKey.Menu or VirtualKey.Shift
            or VirtualKey.LeftWindows or VirtualKey.RightWindows)
        {
            return;
        }

        if (key == VirtualKey.Back)
        {
            SummonHotkeyBox.Text = string.Empty;
            _settings.SummonHotkey = string.Empty;
            Save();
            return;
        }

        var ctrl = IsDown(VirtualKey.Control);
        var alt = IsDown(VirtualKey.Menu);
        var shift = IsDown(VirtualKey.Shift);
        var win = IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows);

        if (!ctrl && !alt && !shift && !win)
        {
            _ = ShowMessageAsync("热键无效", "请至少包含一个修饰键（Ctrl / Alt / Shift / Win）。");
            return;
        }

        var text = HotkeyParser.Format(key, ctrl, alt, shift, win);
        if (!HotkeyParser.TryParse(text, out _, out _))
        {
            _ = ShowMessageAsync("热键无效", $"无法识别组合键：{text}");
            return;
        }

        SummonHotkeyBox.Text = text;
        _settings.SummonHotkey = text;
        Save();
    }

    private void OnResetSummon(object sender, RoutedEventArgs e)
    {
        SummonHotkeyBox.Text = "Alt+Space";
        _settings.SummonHotkey = "Alt+Space";
        Save();
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void Save()
    {
        _settings.Save();
        (Microsoft.UI.Xaml.Application.Current as App)?.RaiseSettingsChanged();
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = "确定",
                XamlRoot = XamlRoot,
            };
            await dialog.ShowAsync();
        }
        catch
        {
            // No XamlRoot yet (page not loaded); the rejection still stands.
        }
    }
}
