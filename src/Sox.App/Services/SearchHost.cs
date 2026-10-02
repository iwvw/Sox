using System.Collections.Concurrent;
using Sox.Core;
using Sox.Core.Indexer.Usn;
using Sox.Core.SearchIndex.Query;
using Sox.Core.Services.Search;
using Sox.PluginSdk.Services;

namespace Sox.App.Services;

    public sealed class SearchHost : IDisposable
    {
        private readonly SearchService _service = new();
        private CancellationTokenSource? _cts;
        private readonly object _gate = new();

        public SearchService Service => _service;

    public async Task<bool> EnsureServiceAsync()
    {
        try
        {
            if (await _service.PingAsync().ConfigureAwait(false))
            {
                return true;
            }
        }
        catch
        {
            // Fall through to the bootstrap path.
        }

        ServiceBootstrapper.TryStart();
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(250).ConfigureAwait(false);
            try
            {
                if (await _service.PingAsync().ConfigureAwait(false))
                {
                    return true;
                }
            }
            catch
            {
                // Keep retrying until the timeout elapses.
            }
        }

        return false;
    }

    public void CancelSearch()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>
    /// Streams results as they arrive. <paramref name="onUpdate"/> is invoked repeatedly (throttled)
    /// with the current best-sorted snapshot, so the UI shows progress without waiting for every
    /// source to finish. It is always invoked off the UI thread.
    /// </summary>
    public async Task SearchStreamingAsync(
        string query,
        Action<IReadOnlyList<SearchResult>> onUpdate,
        Action? onLocalSearchFailed = null,
        int? maxResults = null,
        int throttleMs = 25,
        string? directoryFilter = null)
    {
        var max = maxResults ?? ResolveMaxResults();

        CancellationTokenSource cts;
        lock (_gate)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            cts = new CancellationTokenSource();
            _cts = cts;
        }

        var token = cts.Token;

        var stripped = SearchQuerySortParser.StripExclusionBypass(query, out _);
        var behavior = SearchHistoryStore.Snapshot();
        var penalties = SearchHistoryStore.PenaltySnapshot();
        // The user's folder-priority rules: High/Uncommon become a score bonus, Excluded becomes a hard
        // filter applied in Snapshot. Both come from one settings read per search.
        var priorities = UserSettings.Load().PathPriorities ?? new List<PathPriorityRuleSetting>();
        var comparer = new SearchResultRankComparer(behavior, penalties)
        {
            PathPriorityBonus = PathPriorityResolver.BuildBonus(priorities),
        };

        var bag = new ConcurrentDictionary<string, SearchResult>(StringComparer.OrdinalIgnoreCase);
        var dirty = 0;

        // History is injected as its OWN candidate source, not only used to re-rank what the index
        // returned (that was the bug: a used item the index ranked past the display cap never reached
        // the comparer at all). This matches the upstream behaviour -- the entry the user opened before
        // comes back to the top on the first letter, whether or not its own name still ranks there.
        // Matched against the keyword recorded when it was opened, so a query that prefixes that keyword
        // recalls it. Runs synchronously (a small dictionary scan) before streaming starts, so the first
        // snapshot already carries the history rows.
        if (InjectHistoryMatches(stripped, bag) > 0)
        {
            Interlocked.Exchange(ref dirty, 1);
        }

        // Flush loop: coalesce the concurrent onResult callbacks into periodic sorted snapshots.
        var flushTask = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(throttleMs, token).ConfigureAwait(false);

                if (Interlocked.Exchange(ref dirty, 0) == 0)
                {
                    continue;
                }

                onUpdate(Snapshot(bag, comparer, priorities, max));
            }
        }, token);

        try
        {
            await _service.SearchStreamingAsync(
                stripped,
                max,
                0,
                directoryFilter,
                result =>
                {
                    if (bag.TryAdd(result.Path, result))
                    {
                        Interlocked.Exchange(ref dirty, 1);
                    }
                },
                token,
                onLocalSearchFailed,
                bypassExclusions: false).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when a newer keystroke supersedes this search.
        }
        catch (Exception ex)
        {
            Log.Error("SearchStreamingAsync failed", ex);
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        // Final authoritative flush so the UI ends on the fully sorted set.
        onUpdate(Snapshot(bag, comparer, priorities, max));

        try
        {
            await flushTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Flush loop cancelled with the search; the final snapshot above already ran.
        }
    }

    private static int ResolveMaxResults()
    {
        try
        {
            return Math.Clamp(UserSettings.Load().MaxResults, 1, 10_000);
        }
        catch
        {
            return 200;
        }
    }

    // Recalls history entries whose recorded keyword matches the current query, and seeds them into the
    // result bag before the index stream starts. Uses the same name/alias fzf rule as the index
    // (FuzzyMatcher), so the recall semantics are identical -- an entry opened under "ape" comes back
    // when the user types "a", "ap", "ape", ... The rows carry their real path so the UI opens them
    // normally; their presence in the history map is what the comparer uses to rank them first.
    private static int InjectHistoryMatches(string query, ConcurrentDictionary<string, SearchResult> bag)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return 0;
        }

        IReadOnlyList<HistoryEntry> entries;
        try
        {
            entries = SearchHistoryStore.GetEntries();
        }
        catch (Exception ex)
        {
            Log.Error("Reading search history failed", ex);
            return 0;
        }

        var added = 0;
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Path))
            {
                continue;
            }

            // Application entries are not files: their path is a shell token like
            // "shell:AppsFolder\{AUMID}". Injecting one here made a bogus FILE row whose title was the raw
            // AUMID (Path.GetFileName) and whose subtitle was "shell:AppsFolder" -- and it matched
            // "wows" only because those letters are a subsequence of "Windows". The real app row is
            // produced by ApplicationQueryProvider, and its history boost still applies through the
            // priority cache used by the ranking comparer, so nothing is lost by skipping it here.
            if (entry.Kind == HistoryEntryKind.Application)
            {
                continue;
            }

            var name = Path.GetFileName(entry.Path.TrimEnd('\\'));
            if (string.IsNullOrEmpty(name))
            {
                // A root like "E:\" has no file name; fall back to the path itself.
                name = entry.Path;
            }

            // Recall on the item's own NAME as well as the keyword it was opened under. Matching only
            // the keyword missed an entry whose name the query clearly prefixes but whose recorded
            // keyword differs: "WorldOfWarships.exe" was opened under "worldofwar", so typing "wows"
            // (a subsequence of the NAME, and of nothing in "worldofwar") failed to recall it. The name
            // is what the user is actually reading and abbreviating.
            var matchesKeyword = !string.IsNullOrWhiteSpace(entry.Keyword)
                && Sox.Core.SearchIndex.FuzzyMatcher.IsMatch(query, entry.Keyword);
            if (!matchesKeyword && !Sox.Core.SearchIndex.FuzzyMatcher.IsMatch(query, name))
            {
                continue;
            }

            var result = new SearchResult
            {
                Name = name,
                Path = entry.Path,
                IsDir = entry.Kind == HistoryEntryKind.Folder,
                Drive = Path.GetPathRoot(entry.Path)?.TrimEnd('\\') ?? string.Empty,
            };

            if (bag.TryAdd(result.Path, result))
            {
                added++;
            }
        }

        return added;
    }

    private static List<SearchResult> Snapshot(
        ConcurrentDictionary<string, SearchResult> bag,
        SearchResultRankComparer comparer,
        IReadOnlyList<PathPriorityRuleSetting> priorities,
        int maxResults)
    {
        var hasExclusions = priorities.Any(p => p.Priority == PathPriority.Excluded);
        var list = new List<SearchResult>(bag.Values);
        if (hasExclusions)
        {
            list.RemoveAll(r => PathPriorityResolver.IsExcluded(r.Path, priorities));
        }

        list.Sort(comparer);
        if (list.Count > maxResults)
        {
            list.RemoveRange(maxResults, list.Count - maxResults);
        }

        return list;
    }

    public async Task<IReadOnlyList<SearchResult>> GetRecentAsync(int limit = 20)
    {
        try
        {
            var dirs = ResolveRecentDirectories();
            var files = await _service.GetRecentFilesAsync(dirs, limit, 60 * 24 * 30).ConfigureAwait(false);
            return files;
        }
        catch (Exception ex)
        {
            Log.Error("GetRecentFilesAsync failed", ex);
            return Array.Empty<SearchResult>();
        }
    }

    /// <summary>Bridges the service's index status stream onto the caller (runs until cancelled).</summary>
    public async Task SubscribeStatusAsync(Action<UsnIndexer.IndexerStatus> onStatus, CancellationToken token)
    {
        try
        {
            await SearchStatusStream.SubscribeAsync(onStatus, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            Log.Error("SubscribeStatusAsync failed", ex);
        }
    }

    // ---- Index management (F-17) ----

    public async Task<MachineSettings> GetMachineSettingsAsync() =>
        await _service.GetMachineSettingsAsync().ConfigureAwait(false);

    public async Task<bool> SaveMachineSettingsAsync(MachineSettings settings) =>
        await _service.SaveMachineSettingsAsync(settings).ConfigureAwait(false);

    public async Task<bool> RebuildDriveIndexAsync(string drive) =>
        await _service.RebuildDriveIndexAsync(drive).ConfigureAwait(false);

    public async Task<bool> DeleteDriveIndexAsync(string drive) =>
        await _service.DeleteDriveIndexAsync(drive).ConfigureAwait(false);

    public async Task<bool> CancelDriveIndexAsync(string drive) =>
        await _service.CancelDriveIndexAsync(drive).ConfigureAwait(false);

    public async Task<bool> ClearServiceLogAsync() =>
        await _service.ClearServiceLogAsync().ConfigureAwait(false);

    private static IReadOnlyList<string> ResolveRecentDirectories()
    {
        var dirs = new List<string>();
        void Add(Environment.SpecialFolder folder)
        {
            try
            {
                var path = Environment.GetFolderPath(folder);
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                {
                    dirs.Add(path);
                }
            }
            catch
            {
                // Ignore folders that cannot be resolved.
            }
        }

        Add(Environment.SpecialFolder.Desktop);
        Add(Environment.SpecialFolder.MyDocuments);
        Add(Environment.SpecialFolder.UserProfile);
        return dirs;
    }

    public void Dispose()
    {
        CancelSearch();
        _service.Dispose();
    }
}
