namespace Sox.App.Interop;

internal enum DwmCloakedState
{
    NotCloaked = 0,
    AppCloaked = 1,
    ShellCloaked = 2,
}

internal static class WindowCloak
{
    public static bool Cloak(IntPtr hwnd)
    {
        var value = 1;
        var hr = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAK, ref value, sizeof(int));
        return hr == 0;
    }

    public static void Uncloak(IntPtr hwnd)
    {
        var value = 0;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAK, ref value, sizeof(int));
    }

    public static bool IsCloaked(IntPtr hwnd)
    {
        var hr = NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out var value, sizeof(int));
        return hr == 0 && value != 0;
    }
}
