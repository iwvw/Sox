using System.Text;
using Sox.Core.Hook.InlineSearch;

namespace Sox.Core.Hook;

/// <summary>
/// Resolves a running process's executable name from its id.
/// </summary>
/// <remarks>
/// Deliberately does not reach for <see cref="System.Diagnostics.Process.GetProcessById(int)"/> first. That
/// call enumerates every process on the machine just to validate the id, then hands back a finalizable
/// object -- far more than a name lookup needs on paths that run per window event and per keystroke. The
/// Win32 route here is an open, one query and a close, allocating nothing but the string itself.
/// </remarks>
internal static class ProcessNameResolver
{
    // Unlike PROCESS_QUERY_INFORMATION this is granted for processes running at a higher integrity level
    // than ours, which matters because the hook inspects whatever the user happens to have in front.
    private const uint ProcessQueryLimitedInformation = 0x1000;

    // Consecutive keystrokes and consecutive window events overwhelmingly concern the same process, so one
    // slot collapses a burst into a single lookup. Held only briefly because process ids are reused: after
    // this long, a slot whose process has exited and whose id has been handed to something else is gone.
    private const int CacheLifetimeMs = 1000;

    // The fallback route enumerates every process on the machine, so its result (including "no name")
    // is held far longer than the one-second slot above: still correct for pid reuse in practice, and it
    // keeps that enumeration off the per-keystroke path entirely.
    private const int FailedLookupLifetimeMs = 10_000;

    private sealed record CachedPath(uint ProcessId, string ImagePath, long Ticks);

    private static CachedPath? _cache;
    private static CachedPath? _failedLookups;

    /// <summary>
    /// Full path of the process's executable. False when it cannot be read -- the process has already
    /// exited, or it is a protected/system process we are not allowed to open.
    /// </summary>
    public static bool TryGetImagePath(uint processId, out string imagePath) =>
        TryGetImagePathCore(processId, QueryImagePath, out imagePath);

    // The lookup is passed in so the caching can be exercised without depending on real processes.
    internal static bool TryGetImagePathCore(uint processId, Func<uint, string> queryImagePath, out string imagePath)
    {
        imagePath = string.Empty;
        if (processId == 0)
            return false;

        // Read the slot once. It is only ever replaced wholesale, so a reader on another thread sees either
        // the old entry or the new one intact -- never a process id paired with someone else's path.
        var cached = _cache;
        if (cached is not null && cached.ProcessId == processId && Environment.TickCount64 - cached.Ticks < CacheLifetimeMs)
        {
            imagePath = cached.ImagePath;
            return imagePath.Length > 0;
        }

        var path = queryImagePath(processId);
        if (path.Length == 0)
        {
            // Cache the failure too. Without this every keystroke re-opens a process that will never
            // answer (protected/anti-cheat/system processes, or a recycled pid) -- a full process-list
            // enumeration each time, on the low-level hook thread.
            _cache = new CachedPath(processId, string.Empty, Environment.TickCount64);
            return false;
        }

        _cache = new CachedPath(processId, path, Environment.TickCount64);
        imagePath = path;
        return true;
    }

    private static string QueryImagePath(uint processId)
    {
        var handle = KeyboardNativeMethods.OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero)
            return string.Empty;

        try
        {
            var buffer = new StringBuilder(1024);
            var size = (uint)buffer.Capacity;
            return KeyboardNativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref size)
                ? buffer.ToString()
                : string.Empty;
        }
        finally
        {
            KeyboardNativeMethods.CloseHandle(handle);
        }
    }

    /// <summary>
    /// The executable name with its extension dropped ("explorer"), which is the form
    /// <see cref="System.Diagnostics.Process.ProcessName"/> returns. Plugin adapters compare against it
    /// exactly (<c>processName.Equals("dopus")</c>), so an extension must never leak through.
    /// </summary>
    public static string GetNameWithoutExtension(uint processId, string fallback = "Unknown")
    {
        if (TryGetImagePath(processId, out var imagePath))
            return Path.GetFileNameWithoutExtension(imagePath);

        // The other route below enumerates the whole process list. That is acceptable once, but it must
        // not happen per keystroke on the hook thread: it is gated behind a separate, much longer-lived
        // memo, so a process whose image path is unreadable costs at most one enumeration per interval.
        var now = Environment.TickCount64;
        var failed = _failedLookups;
        if (failed is not null && failed.ProcessId == processId && now - failed.Ticks < FailedLookupLifetimeMs)
            return failed.ImagePath.Length > 0 ? Path.GetFileNameWithoutExtension(failed.ImagePath) : fallback;

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)processId);
            var name = process.ProcessName;
            if (name.Length > 0)
            {
                _failedLookups = new CachedPath(processId, name + ".exe", now);
                return name;
            }
        }
        catch
        {
            // fall through to the recorded fallback
        }

        _failedLookups = new CachedPath(processId, string.Empty, now);
        return fallback;
    }
}
