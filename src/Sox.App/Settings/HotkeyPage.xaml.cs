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
    public HotkeyPage()
    {
        var settings = UserSettings.Load();
        InitializeComponent();
        SummonHotkeyBox.Text = settings.SummonHotkey;
        QuickSwitchHotkeyBox.Text = settings.Hotkeys.QuickSwitchHotkey;
        FullscreenToggle.IsOn = settings.DisableHotkeyInFullscreen;
    }

    private void OnFullscreenToggled(object sender, RoutedEventArgs e)
    {
        UserSettings.Load().DisableHotkeyInFullscreen = FullscreenToggle.IsOn;
        UserSettings.Load().Save();
        (Microsoft.UI.Xaml.Application.Current as App)?.RaiseSettingsChanged();
    }

    private void OnSummonHotkeyKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!TryRecord(e, allowClear: false, out var text))
        {
            if (e.Key == VirtualKey.Back)
                _ = ShowMessageAsync("热键无效", "呼出热键不能为空，请至少包含一个修饰键。");
            return;
        }
        SaveAndSync(summon: text, quickSwitch: null);
    }

    private void OnQuickSwitchHotkeyKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (TryRecord(e, allowClear: true, out var text))
            SaveAndSync(summon: null, quickSwitch: text);
    }

    // Shared recorder: consumes the key, ignores bare modifiers, maps Backspace to "clear" when allowed,
    // and rejects a combo with no modifier. The recorded text uses the same flat format
    // HotkeyParser/ParseCombo read.
    private bool TryRecord(KeyRoutedEventArgs e, bool allowClear, out string text)
    {
        e.Handled = true;
        text = string.Empty;

        var key = e.Key;
        if (key is VirtualKey.Control or VirtualKey.Menu or VirtualKey.Shift
            or VirtualKey.LeftWindows or VirtualKey.RightWindows)
        {
            return false;
        }

        if (key == VirtualKey.Back)
            return allowClear;

        var ctrl = IsDown(VirtualKey.Control);
        var alt = IsDown(VirtualKey.Menu);
        var shift = IsDown(VirtualKey.Shift);
        var win = IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows);

        if (!ctrl && !alt && !shift && !win)
        {
            _ = ShowMessageAsync("热键无效", "请至少包含一个修饰键（Ctrl / Alt / Shift / Win）。");
            return false;
        }

        text = HotkeyParser.Format(key, ctrl, alt, shift, win);
        if (!HotkeyParser.TryParse(text, out _, out _))
        {
            _ = ShowMessageAsync("热键无效", $"无法识别组合键：{text}");
            return false;
        }

        return true;
    }

    private void OnResetHotkeys(object sender, RoutedEventArgs e)
    {
        SaveAndSync(summon: "Alt+Space", quickSwitch: "Ctrl+G");
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    // Saves, then reads the preference back: the main window may reject the combination (already taken)
    // and restore the previous binding, so the box must show what is actually bound, not what was typed.
    private void SaveAndSync(string? summon, string? quickSwitch)
    {
        var settings = UserSettings.Load();
        if (summon != null) settings.SummonHotkey = summon;
        if (quickSwitch != null) settings.Hotkeys.QuickSwitchHotkey = quickSwitch;
        settings.Save();
        (Microsoft.UI.Xaml.Application.Current as App)?.RaiseSettingsChanged();
        var actual = UserSettings.ForceReload();
        SummonHotkeyBox.Text = actual.SummonHotkey;
        QuickSwitchHotkeyBox.Text = actual.Hotkeys.QuickSwitchHotkey;
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
