using System.Collections.Concurrent;
using System.Diagnostics;

namespace Sox.Core.Services.HookLaunch;

// Runs inside the SYSTEM-privileged --service process. Every hook launch -- elevated or not -- goes
// through here rather than the App spawning its own child process, so the App never has to hold a
// runas/UAC fallback of its own. The actual cross-session launch is SessionProcessLauncher's; this type
// adds the one thing specific to hooks: a per-session record of the live process, so repeated requests
// don't spawn duplicates.
public static class HookProcessBroker
{
    private static readonly ConcurrentDictionary<int, Process> _liveHooks = new();
    private static readonly object _liveHooksGate = new();

    public static bool TryLaunch(int sessionId, string exePath, string arguments, bool requestElevation, out int pid, out string? error)
    {
        pid = 0;
        error = null;

        lock (_liveHooksGate)
        {
            if (_liveHooks.TryGetValue(sessionId, out var existing))
            {
                try
                {
                    if (!existing.HasExited)
                    {
                        pid = existing.Id;
                        return true;
                    }
                }
                catch { /* process object stale; fall through and relaunch */ }
                if (_liveHooks.TryRemove(sessionId, out var removed))
                    removed.Dispose();
            }

            if (!SessionProcessLauncher.TryLaunch(sessionId, exePath, arguments, requestElevation, detachFromConsole: true, out pid, out error))
                return false;

            try
            {
                var newProcess = Process.GetProcessById(pid);
                if (_liveHooks.TryGetValue(sessionId, out var previous))
                    previous.Dispose();
                _liveHooks[sessionId] = newProcess;
            }
            catch { /* the hook is running either way; losing the liveness record just allows a relaunch */ }

            return true;
        }
    }

    /// <summary>
    /// Terminates every hook process this service started and clears the registry. Called from
    /// <c>UsnService.OnStop</c>: the hook is the same Sox.Service.exe image, so a surviving hook holds
    /// Service\Sox.Service.exe locked and makes an in-place update fail even after the service itself
    /// reports STOPPED.
    /// </summary>
    public static void KillAll()
    {
        lock (_liveHooksGate)
        {
            foreach (var entry in _liveHooks)
            {
                KillQuietly(entry.Value);
                entry.Value.Dispose();
            }

            _liveHooks.Clear();
        }

        // A hook launched by a previous service process (the service crashed or was restarted) is not in
        // _liveHooks, so sweep every other Sox.Service.exe image by name as well. OnStop runs in the
        // service process, so that process is the only one that must survive.
        var self = Environment.ProcessId;
        foreach (var process in Process.GetProcessesByName("Sox.Service"))
        {
            using (process)
            {
                if (process.Id == self)
                    continue;

                KillQuietly(process);
            }
        }
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch { /* already gone, or access denied; nothing more this process can do */ }
    }
}
