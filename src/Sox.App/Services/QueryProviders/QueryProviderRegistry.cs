namespace Sox.App.Services.QueryProviders;

/// <summary>
/// The set of front-end query providers consulted each keystroke, in registration order. Kept as a small
/// central list (rather than plugin discovery) because the providers are compiled in -- see ADR-0015.
/// </summary>
internal sealed class QueryProviderRegistry : IDisposable
{
    private readonly List<IQueryProvider> _providers = new();

    public static QueryProviderRegistry CreateDefault()
    {
        var registry = new QueryProviderRegistry();
        // Order matters: earlier providers pin their results above later ones. Applications first, then
        // the inline answers (url/search/calculator), then windows, then user commands.
        registry.Add(new ApplicationQueryProvider());
        registry.Add(new WebUrlQueryProvider());
        registry.Add(new WebSearchQueryProvider());
        registry.Add(new CalculatorQueryProvider());
        registry.Add(new WindowQueryProvider());
        registry.Add(new CustomCommandQueryProvider());
        return registry;
    }

    public void Add(IQueryProvider provider) => _providers.Add(provider);

    /// <summary>Forwarded from providers whose results improve asynchronously (web-search suggestions),
    /// so the host can re-run the active scoped query. Raised on a background thread.</summary>
    public event Action? SuggestionsUpdated
    {
        add
        {
            foreach (var provider in _providers)
            {
                if (provider is WebSearchQueryProvider web)
                {
                    web.SuggestionsUpdated += value;
                }
            }
        }
        remove
        {
            foreach (var provider in _providers)
            {
                if (provider is WebSearchQueryProvider web)
                {
                    web.SuggestionsUpdated -= value;
                }
            }
        }
    }

    /// <summary>
    /// Runs every provider for <paramref name="query"/>, each isolated so one throwing cannot take the
    /// others down. Returns them in provider registration order (instant results are pinned above file
    /// results by the caller, not sorted among themselves).
    /// </summary>
    public IEnumerable<InstantResult> Query(string query)
    {
        var results = new List<InstantResult>();
        foreach (var provider in _providers)
        {
            try
            {
                results.AddRange(provider.Query(query));
            }
            catch (Exception ex)
            {
                Log.Error($"Query provider '{provider.GetType().Name}' failed", ex);
            }
        }

        return results;
    }

    /// <summary>
    /// Matches a bare keyword (the token the user typed before a space) to its scope. Used to enter a
    /// scope the moment the user types "g " -- the keyword is consumed into the badge and the box is
    /// cleared, so subsequent typing searches only that provider.
    /// </summary>
    public bool TryMatchKeyword(string keyword, out QueryScope scope, out IQueryProvider provider)
    {
        scope = null!;
        provider = null!;
        foreach (var candidate in _providers)
        {
            foreach (var candidateScope in candidate.Scopes)
            {
                if (string.Equals(candidateScope.Keyword, keyword, StringComparison.OrdinalIgnoreCase))
                {
                    scope = candidateScope;
                    provider = candidate;
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Runs only the given provider (scope mode), returning its results.</summary>
    public IEnumerable<InstantResult> QueryScoped(IQueryProvider provider, string query)
    {
        try
        {
            return provider.Query(query).ToList();
        }
        catch (Exception ex)
        {
            Log.Error($"Scoped query provider '{provider.GetType().Name}' failed", ex);
            return Array.Empty<InstantResult>();
        }
    }

    public void Dispose()
    {
        foreach (var provider in _providers)
        {
            (provider as IDisposable)?.Dispose();
        }

        _providers.Clear();
    }
}
