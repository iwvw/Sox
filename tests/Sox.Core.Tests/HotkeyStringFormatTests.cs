using Sox.Core;
using Xunit;

namespace Sox.Core.Tests;

// The flat hotkey recorder format is parsed on every keystroke by the global hook and on every
// settings save, and gets it wrong silently (a misparse just means the hotkey never fires). These
// pin the two shapes that matter: a bare modifier (double-tap mode) and a Mod+Key combo.
public class HotkeyStringFormatTests
{
    [Theory]
    [InlineData("Ctrl", "Control")]
    [InlineData("Alt", "Alt")]
    [InlineData("Shift", "Shift")]
    [InlineData("Win", "Win")]
    public void IsBareModifier_RecognizesModifierTokens(string value, string expected)
    {
        Assert.True(HotkeyStringFormat.IsBareModifier(value, out var modifier));
        Assert.Equal(expected, modifier);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Ctrl+Space")]
    [InlineData("Space")]
    [InlineData("NotAKey")]
    public void IsBareModifier_RejectsNonModifiers(string? value)
    {
        Assert.False(HotkeyStringFormat.IsBareModifier(value, out var modifier));
        Assert.Equal(string.Empty, modifier);
    }

    [Theory]
    [InlineData("Ctrl+Space", "Control", "Space")]
    [InlineData("Alt+Space", "Alt", "Space")]
    [InlineData("Ctrl+Shift+D", "Control+Shift", "D")]
    [InlineData("Ctrl", "Control", "")]
    [InlineData("F5", "", "F5")]
    public void ParseCombo_SplitsModifierAndKey(string value, string expectedModifier, string expectedKey)
    {
        HotkeyStringFormat.ParseCombo(value, out var modifier, out var key);
        Assert.Equal(expectedModifier, modifier);
        Assert.Equal(expectedKey, key);
    }

    [Theory]
    [InlineData("Win+E")]
    [InlineData("Win+D")]
    [InlineData("Win+Ctrl+D")]
    [InlineData("Win+Shift+S")]
    public void IsReservedWindowsShortcut_DetectsReserved(string value)
    {
        Assert.True(HotkeyStringFormat.IsReservedWindowsShortcut(value));
    }

    [Theory]
    [InlineData("Ctrl+Space")]
    [InlineData("Alt+Space")]
    [InlineData("Ctrl+Shift+D")]
    public void IsReservedWindowsShortcut_AllowsNonWindowsCombos(string value)
    {
        Assert.False(HotkeyStringFormat.IsReservedWindowsShortcut(value));
    }
}
