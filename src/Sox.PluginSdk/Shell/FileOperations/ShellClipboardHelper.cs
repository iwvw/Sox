using System.Runtime.InteropServices;

namespace Sox.PluginSdk.Shell.FileOperations;

/// <summary>
/// Puts file-system items on the clipboard in the same format Explorer uses, so a Paste into any folder
/// (or another app) copies/moves them exactly as if they had been copied there.
/// </summary>
/// <remarks>
/// The clipboard format is <c>CF_HDROP</c> -- a DROPFILES header followed by a double-null-terminated
/// list of UTF-16 paths -- plus the <c>Preferred DropEffect</c> marker that tells the paste target
/// whether this was a Copy or a Move. WPF's <see cref="System.Windows.Clipboard"/> can set text but has
/// no HDROP support, so the format is written directly through Win32.
/// </remarks>
public static class ShellClipboardHelper
{
    // DROPFILES.fWide = 1: the path list is UTF-16, not ANSI.
    private const int DropFilesWide = 1;

    // Preferred DropEffect values (the low word of an OLE DROPEFFECT).
    private const int DropEffectCopy = 1;
    private const int DropEffectMove = 2;

    private const uint GmemMoveable = 0x0002;
    private const uint CfHDrop = 15;

    // The header CF_HDROP puts in front of the path list: DROPFILES = pFiles(4) + POINT(8) + fWide(4),
    // 16 bytes total. Written field-by-field at fixed offsets rather than via a marshalled struct, since
    // the layout is fixed and identical on x86 and x64 (no pointer-sized members).
    private const int DropFilesSize = 16;
    private const int DropFilesOffsetPFiles = 0;
    private const int DropFilesOffsetFWide = 12;

    /// <summary>Copies the paths to the clipboard as a Copy operation.</summary>
    public static void SetCopy(IReadOnlyList<string> paths) => Set(paths, DropEffectCopy);

    /// <summary>Copies the paths to the clipboard as a Move (cut) operation.</summary>
    public static void SetCut(IReadOnlyList<string> paths) => Set(paths, DropEffectMove);

    private static void Set(IReadOnlyList<string> paths, int dropEffect)
    {
        var existing = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (existing.Count == 0) return;

        // Runs on the shell STA worker: the clipboard is an OLE object and opening it from a random
        // thread (or the WinUI UI thread, which does not own an OLE apartment) can fail or block.
        var dispatcher = ShellOperationStaWorker.StaDispatcher;
        if (dispatcher == null)
        {
            Logger.Log("[ShellClipboardHelper] Shell STA worker is unavailable; clipboard was not set.", LogLevel.Error);
            return;
        }

        dispatcher.BeginInvoke(new Action(() => SetCore(existing, dropEffect)));
    }

    private static void SetCore(IReadOnlyList<string> paths, int dropEffect)
    {
        var hDrop = IntPtr.Zero;
        var hEffect = IntPtr.Zero;
        try
        {
            hDrop = BuildHDrop(paths);
            hEffect = BuildDropEffect(dropEffect);

            if (!OpenClipboard(IntPtr.Zero))
            {
                Logger.Log("[ShellClipboardHelper] OpenClipboard failed.", LogLevel.Error);
                return;
            }

            try
            {
                EmptyClipboard();
                // SetClipboardData takes ownership of the handles on success; only free them if the call
                // fails (otherwise the finally below would double-free).
                if (SetClipboardData(CfHDrop, hDrop) != IntPtr.Zero)
                    hDrop = IntPtr.Zero;

                var preferredDropEffect = RegisterClipboardFormat("Preferred DropEffect");
                if (preferredDropEffect != 0 && SetClipboardData(preferredDropEffect, hEffect) != IntPtr.Zero)
                    hEffect = IntPtr.Zero;
            }
            finally
            {
                CloseClipboard();
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"[ShellClipboardHelper] Failed to set clipboard: {ex.Message}", LogLevel.Error);
        }
        finally
        {
            if (hDrop != IntPtr.Zero) GlobalFree(hDrop);
            if (hEffect != IntPtr.Zero) GlobalFree(hEffect);
        }
    }

    private static IntPtr BuildHDrop(IReadOnlyList<string> paths)
    {
        var pathBlock = string.Join('\0', paths) + "\0\0";
        var pathBytes = System.Text.Encoding.Unicode.GetBytes(pathBlock);
        var totalSize = DropFilesSize + pathBytes.Length;

        var hGlobal = GlobalAlloc(GmemMoveable, (UIntPtr)totalSize);
        if (hGlobal == IntPtr.Zero)
            throw new OutOfMemoryException("GlobalAlloc for CF_HDROP failed.");

        var ptr = GlobalLock(hGlobal);
        try
        {
            // pFiles = byte offset of the path list; pt stays zero; fWide = 1 (UTF-16 paths).
            Marshal.WriteInt32(ptr, DropFilesOffsetPFiles, DropFilesSize);
            Marshal.WriteInt32(ptr, DropFilesOffsetFWide, DropFilesWide);
            Marshal.Copy(pathBytes, 0, ptr + DropFilesSize, pathBytes.Length);
        }
        finally
        {
            GlobalUnlock(hGlobal);
        }

        return hGlobal;
    }

    private static IntPtr BuildDropEffect(int effect)
    {
        var hGlobal = GlobalAlloc(GmemMoveable, (UIntPtr)sizeof(int));
        if (hGlobal == IntPtr.Zero)
            throw new OutOfMemoryException("GlobalAlloc for Preferred DropEffect failed.");

        var ptr = GlobalLock(hGlobal);
        try
        {
            Marshal.WriteInt32(ptr, effect);
        }
        finally
        {
            GlobalUnlock(hGlobal);
        }

        return hGlobal;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string lpszFormat);
}
