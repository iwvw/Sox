using System.Text;
using Sox.App.Interop;

namespace Sox.App.Services.QueryProviders;

/// <summary>
/// Enumerates visible top-level windows during a query and offers to switch to the ones whose title
/// matches. Windows are enumerated lazily on each query (there is no cheap cache keyed by window set),
/// but only titles are read, on a short SendMessageTimeout so a hung window cannot stall the UI thread.
/// Ported from Lertaro's WindowSwitcher; the screenshot thumbnail is not ported (icons fall back to the
/// process's exe icon, resolved lazily by ResultItem).
/// </summary>
internal sealed class WindowQueryProvider : IQueryProvider
{
    private const int MaxResults = 6;

    public IEnumerable<InstantResult> Query(string query)
    {
        if (query.Length < 2)
        {
            yield break;
        }

        var matches = new List<(string Title, IntPtr Hwnd, int Tier)>();
        var currentPid = Environment.ProcessId;

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!IsEligible(hwnd, currentPid))
            {
                return true;
            }

            var title = GetWindowTitle(hwnd);
            if (title.Length == 0)
            {
                return true;
            }

            var tier = MatchTier(title, query);
            if (tier < 0)
            {
                return true;
            }

            matches.Add((title, hwnd, tier));
            return true;
        }, IntPtr.Zero);

        matches.Sort(static (a, b) =>
        {
            var c = a.Tier.CompareTo(b.Tier);
            return c != 0 ? c : string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
        });

        foreach (var (title, hwnd, _) in matches.Take(MaxResults))
        {
            yield return new InstantResult
            {
                Id = "win:" + hwnd.ToInt64(),
                Title = title,
                Description = "切换到窗口",
                Glyph = "\uE737",
                LaunchTarget = hwnd.ToInt64().ToString(),
                Action = InstantAction.ActivateWindow,
            };
        }
    }

    // Mirrors the Alt+Tab eligibility rules: visible, unowned, not DWM-cloaked, titled, and either not
    // a tool window or explicitly marked as an app window.
    private static bool IsEligible(IntPtr hwnd, int currentPid)
    {
        if (!NativeMethods.IsWindowVisible(hwnd))
        {
            return false;
        }

        if (NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != IntPtr.Zero)
        {
            return false;
        }

        if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
        {
            return false;
        }

        var exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        var isToolWindow = (exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0;
        var isAppWindow = (exStyle & NativeMethods.WS_EX_APPWINDOW) != 0;
        if (isToolWindow && !isAppWindow)
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == (uint)currentPid)
        {
            return false;
        }

        return true;
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var lengthResult = NativeMethods.SendMessageTimeout(
            hwnd, NativeMethods.WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero,
            NativeMethods.SMTO_ABORTIFHUNG, 150, out var lengthPtr);
        var length = lengthPtr.ToInt32();
        if (lengthResult == IntPtr.Zero || length <= 0)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(length + 1);
        NativeMethods.SendMessageTimeout(
            hwnd, NativeMethods.WM_GETTEXT, (IntPtr)buffer.Capacity, buffer,
            NativeMethods.SMTO_ABORTIFHUNG, 150, out _);
        return buffer.ToString();
    }

    // 0 for a literal title hit, 1 for a fuzzy (subsequence) hit, -1 for no match -- so a window whose
    // title actually contains the query outranks one it merely subsequence-matches.
    private static int MatchTier(string title, string query)
    {
        if (title.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return Sox.Core.SearchIndex.FuzzyMatcher.IsMatch(query, title) ? 1 : -1;
    }
}
