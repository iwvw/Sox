using System.Runtime.InteropServices;
using System.Text;
using Sox.App.Interop;

namespace Sox.App.Services;

/// <summary>
/// Best-effort attempt to put the search box into English (alphanumeric) input mode while it is focused,
/// so an IME candidate window does not pop up during ordinary file search. This uses the legacy IMM32
/// API, which the built-in Microsoft Pinyin honours; TSF-based third-party IMEs (WeChat, Sogou, ...) may
/// ignore it entirely. Every call is logged so the behaviour can be verified per IME. The previous
/// conversion/open status is captured on focus and restored on blur, so the user's IME is only touched
/// while the spotlight is active.
///
/// WinUI 3 hosts text input in a child HWND (the InputSiteWindowClass / DesktopChildSiteBridge), and the
/// IME context usually hangs off that child rather than the top-level window -- targeting the top-level
/// handle is exactly why the ImmAssociateContext approach in microsoft-ui-xaml#7270 appears to do
/// nothing. So the real target is resolved by walking the child windows and picking the first that has an
/// input context.
/// </summary>
internal sealed class ImeController
{
    private readonly IntPtr _topHwnd;
    private bool _captured;
    private int _savedConversion;
    private int _savedSentence;
    private bool _savedOpen;

    public ImeController(IntPtr hwnd) => _topHwnd = hwnd;

    /// <summary>Forces alphanumeric (English) mode. Returns false when no IME context is available.</summary>
    public bool ForceEnglish()
    {
        var target = FindImeWindow();
        if (target == IntPtr.Zero)
        {
            Log.Info("IME: no window with an input context; skipping");
            return false;
        }

        var himc = NativeMethods.ImmGetContext(target);
        if (himc == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (!_captured)
            {
                if (NativeMethods.ImmGetConversionStatus(himc, out _savedConversion, out _savedSentence))
                {
                    _savedOpen = NativeMethods.ImmGetOpenStatus(himc);
                    _captured = true;
                }
            }

            var english = _savedConversion & ~NativeMethods.IME_CMODE_LANGUAGE;
            var conversionOk = NativeMethods.ImmSetConversionStatus(himc, english, _savedSentence);
            var openOk = NativeMethods.ImmSetOpenStatus(himc, false);

            NativeMethods.ImmGetConversionStatus(himc, out var now, out _);
            Log.Info($"IME: force-English hwnd=0x{target.ToInt64():X} conversion={conversionOk} open={openOk} mode=0x{now:X}");
            return conversionOk || openOk;
        }
        finally
        {
            NativeMethods.ImmReleaseContext(target, himc);
        }
    }

    /// <summary>Restores the status captured on the first <see cref="ForceEnglish"/> call.</summary>
    public void Restore()
    {
        if (!_captured)
        {
            return;
        }

        var target = FindImeWindow();
        if (target == IntPtr.Zero)
        {
            _captured = false;
            return;
        }

        var himc = NativeMethods.ImmGetContext(target);
        if (himc == IntPtr.Zero)
        {
            return;
        }

        try
        {
            NativeMethods.ImmSetConversionStatus(himc, _savedConversion, _savedSentence);
            NativeMethods.ImmSetOpenStatus(himc, _savedOpen);
            Log.Info($"IME: restored mode=0x{_savedConversion:X} open={_savedOpen}");
        }
        finally
        {
            NativeMethods.ImmReleaseContext(target, himc);
            _captured = false;
        }
    }

    // The IME context lives on whichever child HWND hosts text input. Prefer the known WinUI input host
    // classes, then fall back to any child that reports a context. Returns IntPtr.Zero when none does.
    private IntPtr FindImeWindow()
    {
        IntPtr best = IntPtr.Zero;
        IntPtr fallback = IntPtr.Zero;

        NativeMethods.EnumChildWindows(_topHwnd, (child, _) =>
        {
            var cls = new StringBuilder(256);
            NativeMethods.GetClassName(child, cls, 256);
            var name = cls.ToString();

            if (fallback == IntPtr.Zero && HasContext(child))
            {
                fallback = child;
            }

            if (name is "InputSiteWindowClass" or "Microsoft.UI.Content.DesktopChildSiteBridge")
            {
                best = child;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        if (best != IntPtr.Zero && HasContext(best))
        {
            return best;
        }

        if (fallback != IntPtr.Zero)
        {
            return fallback;
        }

        // The top-level window itself may carry the context for some IMEs.
        return HasContext(_topHwnd) ? _topHwnd : IntPtr.Zero;
    }

    private static bool HasContext(IntPtr hwnd)
    {
        var himc = NativeMethods.ImmGetContext(hwnd);
        if (himc == IntPtr.Zero)
        {
            return false;
        }

        NativeMethods.ImmReleaseContext(hwnd, himc);
        return true;
    }
}
