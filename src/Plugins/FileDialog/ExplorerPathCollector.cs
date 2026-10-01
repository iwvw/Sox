using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Sox.PluginSdk;
using Sox.PluginSdk.Helpers;
using Sox.PluginSdk.Abstractions.Plugins.WindowAdapters;

namespace Sox.Plugins.FileDialog;

public class ExplorerPathCollector : IActivePathCollector
{
    public string Name => "Windows File Explorer";

    public string TargetName => "Windows File Explorer";

    public bool CanHandle(string className)
    {
        if (string.IsNullOrEmpty(className)) return false;

        return className.Equals("CabinetWClass", StringComparison.OrdinalIgnoreCase) ||
               className.Equals("Progman", StringComparison.OrdinalIgnoreCase) ||
               className.Equals("WorkerW", StringComparison.OrdinalIgnoreCase);
    }

    public string? TryGetPath(IntPtr activeHwnd, string activeClassName, IntPtr windowHwnd, string windowClassName, string processName)
    {
        if (windowHwnd == IntPtr.Zero) return null;

        if (IsDesktopWindow(windowHwnd, windowClassName))
        {
            try
            {
                var path = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                return IsReportedFilesystemPath(path) ? path : null;
            }
            catch
            {
                return null;
            }
        }

        if (windowClassName.Equals("CabinetWClass", StringComparison.OrdinalIgnoreCase))
        {
            return GetActiveExplorerPath(windowHwnd);
        }

        return null;
    }

    // One entry per Explorer tab on current Windows; one per window on classic Explorer.
    public IReadOnlyList<OpenedFolder> GetOpenedFolders()
    {
        IReadOnlyList<OpenedFolder> folders = Array.Empty<OpenedFolder>();
        using var done = new ManualResetEventSlim(false);
        var worker = new Thread(() =>
        {
            try { folders = GetOpenedFoldersCore(); }
            finally { done.Set(); }
        })
        {
            IsBackground = true,
            Name = "ExplorerOpenedFoldersSta"
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();

        return done.Wait(2000) ? folders : Array.Empty<OpenedFolder>();
    }

    private static IReadOnlyList<OpenedFolder> GetOpenedFoldersCore()
    {
        var folders = new List<OpenedFolder>();
        try
        {
            var shellWindowsType = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"));
            if (shellWindowsType == null || Activator.CreateInstance(shellWindowsType) is not object shellWindows)
                return folders;

            try
            {
                dynamic windows = shellWindows;
                var count = (int)windows.Count;
                for (var i = 0; i < count; i++)
                {
                    object? window = null;
                    try
                    {
                        window = windows.Item(i);
                        if (window == null) continue;

                        dynamic explorer = window;
                        var path = explorer.Document.Folder.Self.Path as string;
                        if (IsReportedFilesystemPath(path))
                            folders.Add(new OpenedFolder(path!, (IntPtr)explorer.HWND));
                    }
                    catch { }
                    finally
                    {
                        if (window != null && Marshal.IsComObject(window))
                            Marshal.ReleaseComObject(window);
                    }
                }
            }
            finally
            {
                if (Marshal.IsComObject(shellWindows))
                    Marshal.ReleaseComObject(shellWindows);
            }
        }
        catch { }

        return folders;
    }

    #region Win32 API and COM Helper
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    private interface IComServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid guidService, ref Guid riid, out IntPtr ppvObject);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214E2-0000-0000-C000-000000000046")]
    private interface IShellBrowser
    {
        [PreserveSig]
        int GetWindow(out IntPtr phwnd);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr hwndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    private static bool IsDesktopWindow(IntPtr hwnd, string className)
    {
        if (hwnd == GetShellWindow()) return true;

        if (className.Equals("Progman", StringComparison.OrdinalIgnoreCase))
            return true;

        if (className.Equals("WorkerW", StringComparison.OrdinalIgnoreCase))
        {
            var defView = FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView != IntPtr.Zero)
                return true;
        }

        return false;
    }

    private static bool IsReportedFilesystemPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        !UserPathResolver.IsVirtualPath(path) &&
        !path.Contains("::{", StringComparison.Ordinal) &&
        Path.IsPathRooted(path);

    private static string? GetActiveExplorerPath(IntPtr targetHwnd)
    {
        string? result = null;
        var done = new ManualResetEventSlim(false);
        var worker = new Thread(() =>
        {
            try { result = GetActiveExplorerPathCore(targetHwnd); }
            finally { done.Set(); }
        })
        {
            IsBackground = true,
            Name = "ExplorerPathCollectorSta"
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();

        if (!done.Wait(2000))
        {
            Logger.Log("[ExplorerPathCollector] Timed out waiting for Explorer's COM response.", LogLevel.Warn);
            return null;
        }
        return result;
    }

    private static string? GetActiveExplorerPathCore(IntPtr targetHwnd)
    {
        try
        {
            var activeTabHwnd = IntPtr.Zero;
            EnumChildWindows(targetHwnd, (childHwnd, lParam) =>
            {
                var sbChildClass = new StringBuilder(256);
                GetClassName(childHwnd, sbChildClass, sbChildClass.Capacity);
                if (sbChildClass.ToString().Equals("ShellTabWindowClass", StringComparison.OrdinalIgnoreCase))
                {
                    activeTabHwnd = childHwnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);

            var shellWindowsType = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"));
            if (shellWindowsType == null) return null;

            dynamic shellWindows = Activator.CreateInstance(shellWindowsType)!;
            int count = shellWindows.Count;

            for (var i = 0; i < count; i++)
            {
                try
                {
                    dynamic? window = shellWindows.Item(i);
                    if (window == null) continue;

                    var hwnd = (IntPtr)window.HWND;
                    if (hwnd == targetHwnd)
                    {
                        if (activeTabHwnd != IntPtr.Zero && window is IComServiceProvider serviceProvider)
                        {
                            var serviceId = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
                            var interfaceId = new Guid("000214E2-0000-0000-C000-000000000046");

                            var hr = serviceProvider.QueryService(ref serviceId, ref interfaceId, out var shellBrowserPtr);
                            if (hr == 0 && shellBrowserPtr != IntPtr.Zero)
                            {
                                var shellBrowser = (IShellBrowser)Marshal.GetObjectForIUnknown(shellBrowserPtr);
                                shellBrowser.GetWindow(out var tabHwnd);
                                Marshal.Release(shellBrowserPtr);

                                if (tabHwnd != activeTabHwnd)
                                    continue;
                            }
                        }

                        string path = window.Document.Folder.Self.Path;
                        if (IsReportedFilesystemPath(path))
                            return path;
                    }
                }
                catch { }
            }
        }
        catch { }

        return null;
    }
    #endregion
}
