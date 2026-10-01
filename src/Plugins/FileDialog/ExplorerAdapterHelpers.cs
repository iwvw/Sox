using System.Runtime.InteropServices;
using System.Text;

namespace Sox.Plugins.FileDialog;

internal static class ExplorerAdapterHelpers
{
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr hwndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    public static IntPtr FindContentView(IntPtr explorerHwnd)
    {
        if (explorerHwnd == IntPtr.Zero) return IntPtr.Zero;

        var activeTab = FindWindowEx(explorerHwnd, IntPtr.Zero, "ShellTabWindowClass", null);
        var searchRoot = activeTab != IntPtr.Zero ? activeTab : explorerHwnd;
        var contentView = IntPtr.Zero;
        EnumChildWindows(searchRoot, (childHwnd, _) =>
        {
            var className = new StringBuilder(64);
            GetClassName(childHwnd, className, className.Capacity);
            if (className.ToString().Equals("SHELLDLL_DefView", StringComparison.OrdinalIgnoreCase))
            {
                contentView = childHwnd;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return contentView;
    }
}
