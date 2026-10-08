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
        QuickSwitchHotkeyBox.Text = _settings.Hotkeys.QuickSwitchHotkey;
        FullscreenToggle.IsOn = _settings.DisableHotkeyInFullscreen;
    }

    private void OnFullscreenToggled(object sender, RoutedEventArgs e)
    {
        _settings.DisableHotkeyInFullscreen = FullscreenToggle.IsOn;
        _settings.Save();
        (Microsoft.UI.Xaml.Application.Current as App)?.RaiseSettingsChanged();
    }

    private void OnSummonHotkeyKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (TryRecord(e, out var text))
        {
            _settings.SummonHotkey = text;
            SaveAndSync();
        }
    }

    private void OnQuickSwitchHotkeyKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (TryRecord(e, out var text))
        {
            _settings.Hotkeys.QuickSwitchHotkey = text;
            SaveAndSync();
        }
    }

    // Shared recorder: consumes the key, ignores bare modifiers, maps Backspace to "clear", and rejects a
    // combo with no modifier. The recorded text uses the same flat format HotkeyParser/ParseCombo read.
    private bool TryRecord(KeyRoutedEventArgs e, out string text)
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
        {
            return true;
        }

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
        _settings.SummonHotkey = "Alt+Space";
        _settings.Hotkeys.QuickSwitchHotkey = "Ctrl+G";
        SaveAndSync();
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    // Saves, then reads the preference back: the main window may reject the combination (already taken)
    // and restore the previous binding, so the box must show what is actually bound, not what was typed.
    private void SaveAndSync()
    {
        _settings.Save();
        (Microsoft.UI.Xaml.Application.Current as App)?.RaiseSettingsChanged();
        SummonHotkeyBox.Text = _settings.SummonHotkey;
        QuickSwitchHotkeyBox.Text = _settings.Hotkeys.QuickSwitchHotkey;
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
