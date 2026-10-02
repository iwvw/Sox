using Sox.App.Interop;

namespace Sox.App.Services;

/// <summary>
/// Detects whether a fullscreen window currently owns the foreground, so the summon hotkey can be
/// ignored while a game or video is up (the "disable in fullscreen" preference). A window counts as
/// fullscreen when its rect covers its monitor's full bounds -- the same cheap test most launchers use;
/// it needs no privileges and no per-app list. The taskbar is excluded explicitly, since it spans the
/// screen but is never something to suppress the hotkey for.
/// </summary>
internal static class FullscreenDetector
{
    public static bool IsForegroundFullscreen()
    {
        try
        {
            var fg = NativeMethods.GetForegroundWindow();
            if (fg == IntPtr.Zero)
            {
                return false;
            }

            // The shell/taskbar and the desktop span the screen too; never treat them as fullscreen.
            if (IsShellWindow(fg))
            {
                return false;
            }

            if (!NativeMethods.GetWindowRect(fg, out var rect))
            {
                return false;
            }

            var monitor = NativeMethods.MonitorFromWindow(fg, NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero)
            {
                return false;
            }

            var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (!NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                return false;
            }

            // Compare against the monitor's full bounds (not the work area): a maximized window stops at
            // the work area above the taskbar, so it is correctly NOT fullscreen.
            var bounds = info.rcMonitor;
            const int tolerance = 1;
            return rect.Left <= bounds.Left + tolerance
                && rect.Top <= bounds.Top + tolerance
                && rect.Right >= bounds.Right - tolerance
                && rect.Bottom >= bounds.Bottom - tolerance;
        }
        catch (Exception ex)
        {
            Log.Warning($"Fullscreen detection failed: {ex.Message}");
            return false;
        }
    }

    // The desktop (Progman/WorkerW) and the taskbar (Shell_TrayWnd) cover the screen without being a
    // fullscreen app, so they are filtered out by class name.
    private static bool IsShellWindow(IntPtr hwnd)
    {
        var cls = new System.Text.StringBuilder(64);
        NativeMethods.GetClassName(hwnd, cls, cls.Capacity);
        return cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }
}
