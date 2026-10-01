using System.Runtime.InteropServices;
using Sox.App.Services;

namespace Sox.App.Interop;

internal static class ForegroundHelper
{
    public static void ForceForeground(IntPtr hwnd)
    {
        try
        {
            NativeMethods.AllowSetForegroundWindow(-1);

            var foreground = NativeMethods.GetForegroundWindow();
            if (foreground == hwnd)
            {
                return;
            }

            var currentThread = NativeMethods.GetCurrentThreadId();
            var foregroundThread = foreground == IntPtr.Zero
                ? 0
                : NativeMethods.GetWindowThreadProcessId(foreground, out _);

            var attached = false;
            if (foregroundThread != 0 && foregroundThread != currentThread)
            {
                attached = NativeMethods.AttachThreadInput(foregroundThread, currentThread, true);
            }

            try
            {
                NativeMethods.SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached)
                {
                    NativeMethods.AttachThreadInput(foregroundThread, currentThread, false);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("ForceForeground failed", ex);
        }
    }
}
