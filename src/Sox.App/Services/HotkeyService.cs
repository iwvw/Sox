using Sox.App.Interop;

namespace Sox.App.Services;

/// <summary>
/// Registers the spotlight summon hotkey with the OS and raises <see cref="Pressed"/> when it fires.
/// The window is owned by the caller (its HWND receives WM_HOTKEY); this only manages the registration
/// lifecycle so the hotkey can be re-bound from the settings page without restarting.
/// </summary>
internal sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 1;

    private readonly IntPtr _hwnd;
    private bool _registered;
    private string? _currentHotkey;

    public HotkeyService(IntPtr hwnd) => _hwnd = hwnd;

    public event Action? Pressed;

    /// <summary>The hotkey currently bound, or empty when none is.</summary>
    public string Current => _currentHotkey ?? string.Empty;

    /// <summary>Registers <paramref name="hotkey"/> ("Alt+Space"), replacing any previous binding. An
    /// empty value intentionally clears the binding. Returns false when a non-empty combination is
    /// malformed or already taken by another app; in that case the previous binding is restored rather
    /// than left unbound, so a rejected change never strands the user with no hotkey.</summary>
    public bool Register(string hotkey)
    {
        if (_registered && string.Equals(_currentHotkey, hotkey, StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.IsNullOrWhiteSpace(hotkey))
        {
            Unregister();
            _currentHotkey = string.Empty;
            return true;
        }

        var previous = _currentHotkey;
        Unregister();

        if (TryRegister(hotkey))
        {
            _currentHotkey = hotkey;
            return true;
        }

        // Failed: put the previous binding back so a rejected change never strands the user with no hotkey.
        if (!string.IsNullOrEmpty(previous) && TryRegister(previous))
            _currentHotkey = previous;

        return false;
    }

    private bool TryRegister(string hotkey)
    {
        if (!HotkeyParser.TryParse(hotkey, out var modifiers, out var vk))
        {
            Log.Warning($"Invalid summon hotkey '{hotkey}'; leaving it unbound");
            return false;
        }

        if (!NativeMethods.RegisterHotKey(_hwnd, HotkeyId, modifiers | HotkeyParser.ModNoRepeat, vk))
        {
            var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            Log.Warning($"RegisterHotKey({hotkey}) failed: error {err}");
            return false;
        }

        _registered = true;
        return true;
    }

    private void Unregister()
    {
        if (!_registered)
            return;

        NativeMethods.UnregisterHotKey(_hwnd, HotkeyId);
        _registered = false;
    }

    /// <summary>Called from the window procedure when a WM_HOTKEY for this id arrives.</summary>
    public bool HandleMessage(uint msg, IntPtr wParam)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam == HotkeyId)
        {
            Pressed?.Invoke();
            return true;
        }

        return false;
    }

    public void Dispose() => Unregister();
}
