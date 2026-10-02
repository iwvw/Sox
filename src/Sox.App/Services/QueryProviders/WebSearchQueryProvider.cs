using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using Sox.Core;
using Sox.PluginSdk.Services;

namespace Sox.App.Services.QueryProviders;

/// <summary>
/// Search engines behind a typed keyword: "g 你好" opens a Google search for 你好. Typing the keyword
/// followed by a space enters that engine's scope, which isolates the result list to this provider and
/// badges the search box (see <see cref="Scopes"/>). Search-engine suggestions are fetched in the
/// background and surfaced as extra rows; the direct "search for ..." row is always present so the
/// scope is useful before any suggestion arrives. Engines come from UserSettings.WebSearchEngines, so
/// the set is user-editable (the list was hardcoded before). Ported from Lertaro's WebSearchInstantProvider.
/// </summary>
internal sealed class WebSearchQueryProvider : IQueryProvider, IDisposable
{
    private static readonly HttpClient Http = CreateClient();

    // engine-keyword + term -> suggestions. Bounded: a session types a lot of terms.
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _suggestions = new();
    private readonly ConcurrentDictionary<string, byte> _inFlight = new();

    /// <summary>Raised on a background thread when new suggestions arrive, so the host can re-run the
    /// current scoped query and pick them up. See <see cref="MainWindow"/>'s subscription.</summary>
    public event Action? SuggestionsUpdated;

    private static IReadOnlyList<WebSearchEngineSetting> Engines =>
        UserSettings.Load().WebSearchEngines ?? new List<WebSearchEngineSetting>();

    public IReadOnlyList<QueryScope> Scopes =>
        Engines.Where(e => e.Enabled && !string.IsNullOrWhiteSpace(e.Keyword))
            .Select(e => new QueryScope(
                e.Keyword,
                e.Name,
                e.Glyph,
                string.IsNullOrWhiteSpace(e.IconPath) ? null : e.IconPath))
            .ToList();

    public IEnumerable<InstantResult> Query(string query)
    {
        foreach (var engine in Engines)
        {
            if (!engine.Enabled || string.IsNullOrWhiteSpace(engine.Keyword))
                continue;

            if (!TriggerWord.TryMatchInvoked(query, engine.Keyword, out var argument))
                continue;

            argument = argument.Trim();
            if (argument.Length == 0)
                yield break;

            yield return new InstantResult
            {
                Id = "web:" + engine.Keyword + ":" + argument,
                Title = $"{engine.Name}：{argument}",
                Description = "网页搜索",
                Glyph = engine.Glyph,
                IconPath = string.IsNullOrWhiteSpace(engine.IconPath) ? null : engine.IconPath,
                LaunchTarget = BuildUrl(engine.UrlTemplate, argument),
            };

            foreach (var term in GetSuggestions(engine, argument))
            {
                yield return new InstantResult
                {
                    Id = "web:" + engine.Keyword + ":sug:" + term,
                    Title = term,
                    Description = $"{engine.Name} 搜索",
                    Glyph = engine.Glyph,
                    IconPath = string.IsNullOrWhiteSpace(engine.IconPath) ? null : engine.IconPath,
                    LaunchTarget = BuildUrl(engine.UrlTemplate, term),
                };
            }

            yield break;
        }
    }

    // Returns cached suggestions for the term, kicking off a background fetch on first miss.
    private IReadOnlyList<string> GetSuggestions(WebSearchEngineSetting engine, string term)
    {
        if (string.IsNullOrWhiteSpace(engine.SuggestUrl))
        {
            return Array.Empty<string>();
        }

        var key = engine.Keyword + "\u0000" + term;
        if (_suggestions.TryGetValue(key, out var cached))
        {
            return cached;
        }

        if (_inFlight.TryAdd(key, 0))
        {
            _ = Task.Run(async () =>
            {
                IReadOnlyList<string> result;
                try
                {
                    var url = BuildUrl(engine.SuggestUrl, term);
                    var json = await Http.GetStringAsync(url).ConfigureAwait(false);
                    result = ParseSuggestions(json);
                }
                catch
                {
                    result = Array.Empty<string>();
                }

                if (_suggestions.Count > 512)
                {
                    _suggestions.Clear();
                }

                _suggestions[key] = result;
                _inFlight.TryRemove(key, out _);
                SuggestionsUpdated?.Invoke();
            });
        }

        return Array.Empty<string>();
    }

    private static IReadOnlyList<string> ParseSuggestions(string json)
    {
        var list = new List<string>();
        try
        {
            var payload = StripJsonp(json);
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                // Google / Bing osjson shape: ["query", ["a", "b", ...]].
                if (root.GetArrayLength() >= 2 && root[1].ValueKind == JsonValueKind.Array)
                {
                    AddStrings(root[1], list);
                    return list;
                }

                // Flat array of strings (Baidu's own "s" array once unwrapped).
                AddStrings(root, list);
                return list;
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                // Baidu's callback object: {q:"...", s:["a","b"]}. Try the usual property names.
                foreach (var name in (string[])["s", "suggestions", "results", "data"])
                {
                    if (root.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        AddStrings(arr, list);
                        if (list.Count > 0)
                        {
                            return list;
                        }
                    }
                }
            }
        }
        catch
        {
            // Malformed or a different shape; no suggestions is fine.
        }

        return list;
    }

    /// <summary>Returns the JSON inside a JSONP wrapper, or the payload unchanged when it is already
    /// JSON. Detected from the text, not the engine, so any JSONP suggestion endpoint works.</summary>
    private static string StripJsonp(string json)
    {
        var trimmed = json.TrimStart();
        if (trimmed.StartsWith("[", StringComparison.Ordinal) || trimmed.StartsWith("{", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var open = json.IndexOf('(');
        var close = json.LastIndexOf(')');
        if (open >= 0 && close > open)
        {
            return json[(open + 1)..close];
        }

        var start = json.IndexOf('[', StringComparison.Ordinal);
        var end = json.LastIndexOf(']');
        return start >= 0 && end > start ? json[start..(end + 1)] : json;
    }

    private static void AddStrings(JsonElement array, List<string> list)
    {
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var text = item.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    list.Add(text);
                }
            }
        }
    }

    private static string BuildUrl(string template, string value)
    {
        var encoded = Uri.EscapeDataString(value);
        if (template.Contains("%s", StringComparison.Ordinal))
        {
            return template.Replace("%s", encoded, StringComparison.Ordinal);
        }

        if (template.Contains("{0}", StringComparison.Ordinal))
        {
            return template.Replace("{0}", encoded, StringComparison.Ordinal);
        }

        return template + encoded;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        try
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
        }
        catch
        {
        }

        return client;
    }

    public void Dispose() => Http.Dispose();
}
