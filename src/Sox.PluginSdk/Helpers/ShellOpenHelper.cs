using System.Runtime.InteropServices;

namespace Sox.PluginSdk.Helpers;

/// <summary>
/// The one place this app hands "open this folder" and "show me that item" to the Windows shell.
/// </summary>
/// <remarks>
/// Every call site used to spell the shell's own command line instead
/// (<c>Process.Start("explorer.exe", $"/select,\"{path}\"")</c>), which is wrong three ways: it forces
/// explorer.exe even when the user registered a replacement file manager, the <c>/select,</c> switch is
/// an unparsed string that a quote or trailing backslash in the path breaks, and it starts a whole
/// second process to ask the shell for something this process can ask over the API those command lines
/// end up calling anyway. <c>ShellExecuteW</c> is the launcher verb route, <c>SHOpenFolderAndSelectItems</c>
/// is the documented "reveal this item" route.
/// </remarks>
public static class ShellOpenHelper
{
    private const int SwShowNormal = 1;

    // ShellExecuteW's documented success test: the returned HINSTANCE is a value greater than 32, and
    // anything at or below it is one of its own error codes.
    private const int ShellExecuteSuccessThreshold = 32;

    // SHParseDisplayName needs the shell's own item id for the target, and it is the reason a virtual
    // token ("shell:AppsFolder", "::{CLSID}") works here at all -- it parses shell names, not file
    // system ones.
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr ShellExecuteW(
        IntPtr hwnd,
        string? lpOperation,
        string lpFile,
        string? lpParameters,
        string? lpDirectory,
        int nShowCmd);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszName,
        IntPtr pbc,
        out IntPtr ppidl,
        uint sfgaoIn,
        out uint psfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl, IntPtr[]? apidl, uint dwFlags);

    // SEE_MASK_INVOKEIDLIST (0x0000000C) is what makes ShellExecuteEx resolve a shell CONTEXT-MENU verb
    // like "properties": without it the shell only knows the static registered verbs (open, edit, ...)
    // and silently ignores "properties", which is why Process.Start(Verb: "properties") did nothing.
    private const uint SeeMaskInvokeIdList = 0x0000000C;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpVerb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShellExecuteExW(ref SHELLEXECUTEINFO lpExecInfo);

    /// <summary>
    /// Shows the shell's Properties dialog for <paramref name="itemPath"/> (the same one the Explorer
    /// context menu's 属性 opens).
    /// </summary>
    /// <returns><see langword="false"/> when the shell could not show it.</returns>
    public static bool TryShowProperties(string? itemPath)
    {
        if (string.IsNullOrWhiteSpace(itemPath)) return false;

        try
        {
            var info = new SHELLEXECUTEINFO
            {
                cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
                fMask = SeeMaskInvokeIdList,
                hwnd = IntPtr.Zero,
                lpVerb = "properties",
                lpFile = itemPath,
                nShow = SwShowNormal,
            };
            return ShellExecuteExW(ref info);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Opens <paramref name="folderPath"/> the way a double-click would: whatever the user has registered
    /// for folders (Explorer, or a replacement file manager) decides what happens.
    /// </summary>
    /// <returns><see langword="false"/> when the shell refused the request, or nothing was asked for.</returns>
    public static bool TryOpenFolder(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return false;
        try
        {
            return (long)ShellExecuteW(IntPtr.Zero, "open", folderPath, null, null, SwShowNormal) > ShellExecuteSuccessThreshold;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Shows the folder holding <paramref name="itemPath"/> with that item selected. A folder passed in
    /// is revealed in its parent the same way a file is, so callers do not need to know which they hold.
    /// </summary>
    /// <returns><see langword="false"/> when the shell could not resolve or reveal the item.</returns>
    public static bool TryRevealInFolder(string? itemPath)
    {
        if (string.IsNullOrWhiteSpace(itemPath)) return false;

        var pidl = IntPtr.Zero;
        try
        {
            if (SHParseDisplayName(itemPath, IntPtr.Zero, out pidl, 0, out _) != 0 || pidl == IntPtr.Zero)
                return false;

            // Selecting the item itself (cidl 0, apidl null) is the "open the parent and highlight me"
            // call; the shell reuses an existing Explorer window on the matching monitor when it can.
            return SHOpenFolderAndSelectItems(pidl, 0, null, 0) == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
        }
    }
}
