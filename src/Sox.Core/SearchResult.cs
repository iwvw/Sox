using Sox.PluginSdk.Abstractions;

namespace Sox.Core;


public class SearchResult : ISearchResult
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;

    /// <summary>Plugin SDK contract alias for <see cref="Path"/>.</summary>
    public string FullPath => Path;

    /// <summary>Plugin SDK contract: the directory a result lives in (itself for folders).</summary>
    public string ContextDirectory => IsDir ? Path : System.IO.Path.GetDirectoryName(Path) ?? Drive + ":\\";

    public bool IsDir { get; set; }
    public string Drive { get; set; } = string.Empty;

    public bool IsApplication => false;

    public FileAttributes Attributes { get; set; }
    internal ulong RankSortKey { get; set; }

    // Memo for SearchResultRankComparer.BehaviorScore. The comparer is called O(n log n) times per sort
    // and recomputed the same path's normalization plus two dictionary probes on every call; the score
    // depends only on the instance's own path, so it is stable for the lifetime of the result (a fresh
    // SearchResult is produced per search). Nullable so "not yet computed" is distinct from a real 0.
    internal double? BehaviorScoreCache { get; set; }

    // Memo for "is this path in the history map" -- the comparer's primary key (ADR-0018). Cached for
    // the same reason as BehaviorScoreCache: O(n log n) probes of the same instance per sort.
    internal bool? CuratedCache { get; set; }

    // Populated from the index for every result that comes from one (local or network/WSL) -- lets
    // SearchService merge GetRecentFiles' local and network/WSL result sets by actual recency instead of
    // just concatenating two already-sorted-but-incomparable lists, lets the sidebar Size/Date filters and
    // the Size column run synchronously off already-known data instead of a per-application IPC round
    // trip (see FileSizeFilterProvider/DateModifiedFilterProvider), and lets the UI show a modified date
    // without re-touching the filesystem (see AppSearchResult.DateModified). Default for results that
    // don't come from an index (plugin results, etc.) or genuinely have no recorded metadata.
    public FileMetadata Metadata { get; set; }
}

public sealed class SearchResultRankComparer : IComparer<SearchResult>
{
    public static readonly SearchResultRankComparer Instance = new(
        new Dictionary<string, double>(), new Dictionary<string, int>());

    private readonly IReadOnlyDictionary<string, double> _behaviorScores;
    private readonly IReadOnlyDictionary<string, int> _penalties;

    /// <summary>Directory the search is taking place in (an open dialog's folder), for the context bonus.</summary>
    public string? ContextDirectory { get; init; }

    public SearchResultRankComparer(IReadOnlyDictionary<string, double> behaviorScores)
        : this(behaviorScores, new Dictionary<string, int>())
    {
    }

    public SearchResultRankComparer(
        IReadOnlyDictionary<string, double> behaviorScores,
        IReadOnlyDictionary<string, int> penalties)
    {
        _behaviorScores = behaviorScores;
        _penalties = penalties;
    }

    private static string NormalizeForLookup(string path)
    {
        if (path.Length > 3 && path[^1] == '\\')
            return path.TrimEnd('\\');
        return path;
    }

    public int Compare(SearchResult? left, SearchResult? right)
    {
        if (ReferenceEquals(left, right))
            return 0;
        if (left == null)
            return 1;
        if (right == null)
            return -1;

        // History wins first: a path the user has opened before (and that matches the current query at
        // all -- everything here came from a match) ranks above anything never used. Within the used set
        // the behaviour score decides; only then does textual relevance break ties. See ADR-0018 (this
        // revises ADR-0013, which had relevance's start position outrank history entirely).
        var compare = IsCurated(right).CompareTo(IsCurated(left)); // curated first
        if (compare != 0)
            return compare;

        compare = BehaviorScore(right).CompareTo(BehaviorScore(left)); // higher score first
        if (compare != 0)
            return compare;

        compare = left.RankSortKey.CompareTo(right.RankSortKey);
        if (compare != 0)
            return compare;

        compare = left.Path.Length.CompareTo(right.Path.Length);
        if (compare != 0)
            return compare;

        compare = string.Compare(left.Drive, right.Drive, StringComparison.OrdinalIgnoreCase);
        if (compare != 0)
            return compare;

        return string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);
    }

    private double BehaviorScore(SearchResult result)
    {
        if (result.BehaviorScoreCache is { } cached)
            return cached;

        var path = NormalizeForLookup(result.Path);
        var score = _behaviorScores.TryGetValue(path, out var s) ? s : 0;
        if (_penalties.TryGetValue(path, out var passovers))
            score -= HistoryRankWeights.Penalty(passovers);
        if (ContextDirectory is { Length: > 0 } ctx &&
            string.Equals(result.ContextDirectory.TrimEnd('\\'), ctx.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            score += HistoryRankWeights.ContextBonus;

        result.BehaviorScoreCache = score;
        return score;
    }

    private bool IsCurated(SearchResult result)
    {
        if (result.CuratedCache is { } cached)
            return cached;

        var curated = _behaviorScores.ContainsKey(NormalizeForLookup(result.Path));
        result.CuratedCache = curated;
        return curated;
    }
}
