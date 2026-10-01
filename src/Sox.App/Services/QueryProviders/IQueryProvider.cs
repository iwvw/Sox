namespace Sox.App.Services.QueryProviders;

/// <summary>
/// A front-end result source consulted on every keystroke, in addition to the core file search.
/// Implementations must be synchronous and cheap (no blocking IO on the UI thread); anything expensive
/// (enumerating the start menu, reading disk) belongs in a background cache the provider reads from.
/// Each provider is isolated by the host, so one throwing cannot break the others. See ADR-0015.
/// </summary>
internal interface IQueryProvider
{
    /// <summary>Results for <paramref name="query"/> (already trimmed, non-empty). May return empty.</summary>
    IEnumerable<InstantResult> Query(string query);

    /// <summary>
    /// Scopes this provider offers: typing one of their keywords followed by a space enters an isolated
    /// mode that shows only this provider's results and badges the search box (Listary's behaviour).
    /// Empty for providers that only ever contribute inline results (applications, windows). A provider
    /// may declare several (WebSearch declares one per engine, so "g " and "bing " are distinct scopes).
    /// </summary>
    IReadOnlyList<QueryScope> Scopes => [];
}
