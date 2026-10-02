namespace Sox.Core;

/// <summary>
/// Ranking weight a path gets on top of the behaviour score. Mirrors Listary's priority tiers:
/// high paths float up, uncommon paths sink, excluded paths are filtered out, normal is the default.
/// </summary>
public enum PathPriority
{
    High,
    Normal,
    Uncommon,
    Excluded,
}

/// <summary>One folder (or file) and the priority its whole subtree should get.</summary>
public class PathPriorityRuleSetting
{
    public string Path { get; set; } = string.Empty;
    public PathPriority Priority { get; set; } = PathPriority.Normal;
}

/// <summary>
/// A user-editable web-search engine: typing its keyword then a space scopes the result list to it.
/// Replaces the previously hardcoded source list (see the web-search query provider).
/// </summary>
public class WebSearchEngineSetting
{
    public bool Enabled { get; set; } = true;
    public string Keyword { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>Search URL with a <c>%s</c> placeholder for the encoded query.</summary>
    public string UrlTemplate { get; set; } = string.Empty;

    /// <summary>Optional suggestion endpoint with the same <c>%s</c> placeholder; empty disables suggestions.</summary>
    public string SuggestUrl { get; set; } = string.Empty;

    /// <summary>Optional local icon file (ico/png). When empty, <see cref="Glyph"/> is used.</summary>
    public string IconPath { get; set; } = string.Empty;

    /// <summary>Segoe Fluent glyph shown when there is no icon file.</summary>
    public string Glyph { get; set; } = "\uE721";
}

/// <summary>The built-in web-search engines a fresh settings file starts with.</summary>
public static class WebSearchDefaults
{
    public static List<WebSearchEngineSetting> Create() =>
    [
        new() { Keyword = "bd", Name = "百度", UrlTemplate = "https://www.baidu.com/s?wd=%s", SuggestUrl = "https://suggestion.baidu.com/su?wd=%s&cb=window.bdsug.sug", IconPath = "simple-icons:baidu" },
        new() { Keyword = "g", Name = "Google", UrlTemplate = "https://www.google.com/search?q=%s", SuggestUrl = "https://suggestqueries.google.com/complete/search?client=firefox&q=%s", IconPath = "logos:google-icon" },
        new() { Keyword = "bing", Name = "Bing", UrlTemplate = "https://www.bing.com/search?q=%s", SuggestUrl = "https://api.bing.com/osjson.aspx?query=%s", IconPath = "logos:bing" },
        new() { Keyword = "gh", Name = "GitHub", UrlTemplate = "https://github.com/search?q=%s", IconPath = "logos:github-icon" },
        new() { Keyword = "wiki", Name = "Wikipedia", UrlTemplate = "https://zh.wikipedia.org/wiki/Special:Search?search=%s", IconPath = "simple-icons:wikipedia" },
        new() { Keyword = "yt", Name = "YouTube", UrlTemplate = "https://www.youtube.com/results?search_query=%s", IconPath = "logos:youtube-icon" },
    ];
}

/// <summary>
/// Turns the priority rules into a per-path bonus for the rank comparer, and answers whether a path is
/// hard-excluded. Prefix matching, case-insensitive, boundary-aware so "C:\Foo" does not match "C:\Foobar".
/// </summary>
public static class PathPriorityResolver
{
    public const double HighBonus = 400;
    public const double UncommonPenalty = 400;

    public static string Normalize(string path) =>
        path.Trim().Trim('"').Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar);

    private static bool IsUnder(string normalizedPath, string normalizedRoot) =>
        normalizedPath.Length == normalizedRoot.Length
            ? normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            : normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
              && normalizedPath[normalizedRoot.Length] == Path.DirectorySeparatorChar;

    /// <summary>The most specific (longest) matching rule for a path, or null when none applies.</summary>
    public static PathPriorityRuleSetting? Match(string path, IReadOnlyList<PathPriorityRuleSetting> rules)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var normalized = Normalize(path);
        PathPriorityRuleSetting? best = null;
        var bestLen = -1;
        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Path))
                continue;

            var root = Normalize(rule.Path);
            if (root.Length > bestLen && IsUnder(normalized, root))
            {
                best = rule;
                bestLen = root.Length;
            }
        }

        return best;
    }

    /// <summary>A bonus function for <c>SearchResultRankComparer.PathPriorityBonus</c>, or null when no
    /// High/Uncommon rule exists (nothing to add).</summary>
    public static Func<string, double>? BuildBonus(IReadOnlyList<PathPriorityRuleSetting> rules)
    {
        var effective = rules
            .Where(r => !string.IsNullOrWhiteSpace(r.Path) && r.Priority is PathPriority.High or PathPriority.Uncommon)
            .ToList();
        if (effective.Count == 0)
            return null;

        return path => Match(path, effective)?.Priority switch
        {
            PathPriority.High => HighBonus,
            PathPriority.Uncommon => -UncommonPenalty,
            _ => 0,
        };
    }

    /// <summary>True when the path falls under any rule marked <see cref="PathPriority.Excluded"/>.</summary>
    public static bool IsExcluded(string path, IReadOnlyList<PathPriorityRuleSetting> rules) =>
        Match(path, rules)?.Priority == PathPriority.Excluded;
}
