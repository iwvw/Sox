using System.Diagnostics;

using Sox.Core.Indexer.NetworkDrive.Walk;
namespace Sox.Core.Indexer.NetworkDrive.Scheduling;

// The actual per-drive scan pass, extracted out of Scheduler (composition, not a partial class) to keep
// that type's files under the project's line limit. Takes Scheduler's own status/checkpoint/completion
// callbacks as explicit parameters rather than reaching into Scheduler's private fields.
internal static class DriveRefreshRunner
{
    public static void RefreshDrive(
        string drive,
        CancellationToken token,
        Action<string, string, int?, string?> setStatus,
        Func<string, FileRecordStore?> getPreviousStore,
        Action<string, FileRecordStore, NetworkDriveWalkStats, CancellationToken> onPublishCheckpoint,
        Action<string, NetworkIndex> onRefreshFinished,
        Action<string> releaseCachedIndex)
    {
        var root = PathHelpers.BuildSourceRoot(drive);
        var physicalRoot = root;

        // A drive/share that's temporarily offline (virtual disk unmounted, network hiccup) enumerates
        // its root as empty rather than throwing loudly -- TreeBuilder.WalkDirectory just CountErrors and
        // returns, so the walk below "succeeds" with a near-empty store (just the root record), and would
        // unconditionally overwrite a good, previously-built cache with it. Bailing out here before
        // touching status/building anything leaves the existing cache and status completely untouched,
        // so it just gets picked up again next scheduled/manual refresh once the drive is back.
        if (!Directory.Exists(physicalRoot))
        {
            Logger.Log($"[NetworkIndexer] {drive}: root is currently unreachable, skipping this refresh (existing cache left untouched).", LogLevel.Warn);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            setStatus(drive, "indexing", 0, null);
            var settings = UserSettings.Load();
            var options = new WalkOptions(
                settings.ExcludedPaths,
                settings.IgnoredPathGlobs,
                settings.IgnoredPathRegexes,
                0,
                0,
                true);
            var previousStore = getPreviousStore(drive);
            LogResumeProgress(drive, previousStore);
            var index = NetworkIndex.Build(
                drive,
                root,
                physicalRoot,
                options,
                token,
                // Fires every ~1024 items; unguarded, this can race past CancelDrive's "cached" revert
                // and clobber it back to "indexing" -- what made the Stop button lose that race sometimes.
                (files, dirs) => { if (!token.IsCancellationRequested) setStatus(drive, "indexing", files + dirs, null); },
                (store, stats) => onPublishCheckpoint(drive, store, stats, token),
                previousStore,
                beforeFinalWrite: () => releaseCachedIndex(drive));
            token.ThrowIfCancellationRequested();
            // Reaching here without cancellation only means TreeBuilder.Run() drained its queue -- NOT
            // that every directory's real contents were captured. A directory that failed to enumerate
            // (network hiccup, permissions) is caught silently in WalkDirectory (CountError + return,
            // never MarkListed), so it stays un-Listed for a future rebuild to retry regardless.
            //
            // Requiring zero errors would make IsComplete functionally never true on a large tree, so a
            // few transient failures are tolerated. But when the share drops mid-scan almost every
            // directory fails, and accepting that would persist a near-empty snapshot as "complete" --
            // Configure then trusts it and stops looking. Treat a preponderance of failures as "this
            // pass captured nothing" and leave the previous cache as the source of truth.
            var attempted = index.Count + index.EnumerateErrors;
            var mostlyFailed = attempted > 0 && index.EnumerateErrors * 2 > attempted;
            index.IsComplete = !mostlyFailed;
            if (mostlyFailed)
            {
                Logger.Log($"[NetworkIndexer] {drive}: refresh failed for most directories ({index.EnumerateErrors} enumerate error(s) out of {attempted}), leaving IsComplete=false so the next pass retries instead of trusting a near-empty snapshot.", LogLevel.Warn);
            }
            else if (index.Errors > 0)
            {
                Logger.Log($"[NetworkIndexer] {drive}: finished with {index.Errors} error(s) ({index.EnumerateErrors} enumerate, {index.AttributeErrors} attribute) -- marking complete anyway; affected directories stay un-Listed for a future manual rebuild to retry.", LogLevel.Warn);
            }
            IndexerHelper.Save(index);

            stopwatch.Stop();
            Logger.Log($"[NetworkIndexer] {drive}: finished in {stopwatch.Elapsed.TotalSeconds:F1}s, {index.Count} records.");

            onRefreshFinished(drive, index);
        }
        catch (OperationCanceledException)
        {
            Logger.Log($"[NetworkIndexer] {drive}: refresh cancelled, keeping the last checkpoint.");
            // A drive removed from config already had its status entry deleted (NetworkIndexer.Configure),
            // so SetStatus's own "only update an entry that still exists" guard makes this a no-op there --
            // this only actually reverts the status for a drive a user stopped via CancelDrive while it
            // remains configured, so it shows what's on disk from the last checkpoint instead of being
            // stuck on "indexing" forever.
            setStatus(drive, "cached", null, null);
        }
        catch (Exception ex)
        {
            Logger.Log($"[NetworkIndexer] Failed to index {drive}: {ex.Message}", LogLevel.Error);
            setStatus(drive, "error", null, ex.Message);
        }
    }

    // Directories-listed ratio is a proxy for "how far the previous pass got": a directory only carries
    // FileRecordFlags.Listed once its own children were fully captured, so this is what TreeDiffBaseline
    // will actually be able to trust and skip re-listing, as opposed to just the raw record count.
    private static void LogResumeProgress(string drive, FileRecordStore? previousStore)
    {
        if (previousStore == null)
        {
            Logger.Log($"[NetworkIndexer] {drive}: no previous index to resume from, starting a fresh scan.");
            return;
        }

        var totalDirs = 0;
        var listedDirs = 0;
        foreach (var record in previousStore.Records)
        {
            if (!record.IsDirectory)
                continue;
            totalDirs++;
            if ((record.Flags & FileRecordFlags.Listed) != 0)
                listedDirs++;
        }

        Logger.Log($"[NetworkIndexer] {drive}: resuming with {previousStore.Records.Count} records from last pass " +
            $"({listedDirs}/{totalDirs} directories confirmed listed, previous IsComplete={previousStore.IsComplete}).");
    }
}
