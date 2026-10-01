using Sox.Core.IndexV2;

using Sox.Core.IndexV2.Search;

namespace Sox.Core.Indexer.Usn;

// Fans a query out across every local drive's LiveIndex. Unlike the old RuntimeIndex-based coordinator
// (which held UsnIndexer's single coarse lock for the ENTIRE search, serializing all drives' searches
// against each other AND against USN update application), each LiveIndex now owns its own
// reader-writer lock -- so the outer lock here only protects the brief `.ToArray()` snapshot of which
// drives currently exist, not the search work itself. Strictly finer-grained than before, never coarser.
internal static class SearchCoordinator
{
    public static void SearchStreaming(
        Dictionary<string, LiveIndex> recordIndexes,
        object lockObj,
        string query,
        int limit,
        Action<SearchResult> onResult,
        CancellationToken token,
        string? directoryFilter,
        string? fileNameFilter = null)
    {
        LiveIndex[] drives;
        lock (lockObj)
        {
            drives = recordIndexes.Values.ToArray();
        }

        if (drives.Length == 0)
            return;

        if (drives.Length == 1)
        {
            IndexV2Searcher.SearchStreaming(drives[0], query, limit, onResult, token, directoryFilter, fileNameFilter);
            return;
        }

        // Divide the machine's cores across the drives instead of letting each drive's inner match fan
        // out to the full logical-core count on its own: the per-drive Parallel.For used to run at its
        // default degree (≈logical cores) for EVERY drive at once, so a 3-drive box scanned with ~72
        // threads on 16 cores and lost more to context switching than the extra workers won.
        var maxDrives = Math.Min(drives.Length, Math.Clamp(Environment.ProcessorCount, 2, 8));
        var perDriveParallelism = Math.Max(1, Environment.ProcessorCount / maxDrives);

        // Each drive resolves and streams its OWN top `limit`; the merge and final cut happen downstream
        // on the shared RankSortKey. A single cross-drive budget was wrong: whichever drive finished
        // first spent it, so a strong match on a later drive (酒馆 on E:, when C:/D: streamed first) was
        // dropped before its turn -- exactly the "the thing I want is missing while unrelated files show"
        // symptom. One budget per drive only costs each drive doing its normal top-N, which it already
        // does internally; the consumer caps the merged set.
        Parallel.For(
            0,
            drives.Length,
            new ParallelOptions
            {
                CancellationToken = token,
                MaxDegreeOfParallelism = maxDrives
            },
            i =>
            {
                token.ThrowIfCancellationRequested();

                IndexV2Searcher.SearchStreaming(drives[i], query, limit, result =>
                {
                    token.ThrowIfCancellationRequested();
                    onResult(result);
                }, token, directoryFilter, fileNameFilter, perDriveParallelism);
            });
    }

    // No fan-out here, unlike the search above: a path lives on exactly one drive, and every other
    // drive's index rejects it on the source-root prefix check before doing any work.
    public static bool EnumerateDirectory(
        Dictionary<string, LiveIndex> recordIndexes,
        object lockObj,
        string path,
        bool recursive,
        string[]? patterns,
        int limit,
        Action<SearchResult> onResult,
        CancellationToken token)
    {
        LiveIndex[] drives;
        lock (lockObj)
        {
            drives = recordIndexes.Values.ToArray();
        }

        foreach (var drive in drives)
        {
            token.ThrowIfCancellationRequested();
            if (IndexV2Searcher.EnumerateDirectory(drive, path, recursive, patterns, limit, onResult, token))
                return true;
        }
        return false;
    }

    // IndexV2 has no cross-search rank/candidate cache yet (a known follow-up, not a correctness gap
    // -- see the IndexV2 migration notes); kept as a no-op call site so callers don't need to know that.
    public static void ClearCaches()
    {
    }
}
