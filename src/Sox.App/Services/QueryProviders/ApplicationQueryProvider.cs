using Sox.PluginSdk.Helpers;

namespace Sox.App.Services.QueryProviders;

/// <summary>
/// Start-menu and desktop applications, matched by name. Enumeration (start menu + desktop, all user
/// profiles) is expensive, so it runs once on a background thread and is cached; <see cref="Query"/>
/// only scans that cache. Reuses the core <see cref="StartMenuShortcutResolver"/> for the roots, the
/// file filter and .lnk target resolution, so this stays a thin front-end source rather than a second
/// implementation of the same shell knowledge (ADR-0015).
/// </summary>
internal sealed class ApplicationQueryProvider : IQueryProvider, IDisposable
{
    private const int MaxResults = 8;

    private readonly object _gate = new();
    private IReadOnlyList<Entry> _entries = Array.Empty<Entry>();
    private volatile bool _loaded;

    private sealed record Entry(string Title, string LaunchTarget, string IconPath);

    public ApplicationQueryProvider()
    {
        _ = Task.Run(Load);
    }

    private void Load()
    {
        try
        {
            var entries = new List<Entry>();
            var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var root in StartMenuShortcutResolver.GetStartMenuRoots())
            {
                foreach (var file in StartMenuShortcutResolver.EnumerateFilesSafe(root))
                {
                    if (!StartMenuShortcutResolver.ShouldIndex(file))
                    {
                        continue;
                    }

                    var title = Path.GetFileNameWithoutExtension(file);
                    if (string.IsNullOrWhiteSpace(title) || !seenTitles.Add(title))
                    {
                        continue;
                    }

                    // A .lnk's icon carries the shortcut overlay; the target's does not, so prefer the
                    // target for the icon when it resolves to something on disk.
                    var target = StartMenuShortcutResolver.ResolveShortcutTarget(file) ?? file;
                    if (target.Length > 0 && !seenTargets.Add(target))
                    {
                        continue;
                    }

                    var iconPath = File.Exists(target) ? target : file;
                    entries.Add(new Entry(title, file, iconPath));
                }
            }

            AppendAppsFolderApps(entries, seenTitles);

            lock (_gate)
            {
                _entries = entries;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Application enumeration failed", ex);
        }
        finally
        {
            _loaded = true;
        }
    }

    // Modern packaged (UWP/MSIX) apps -- Notepad, Calculator, Terminal -- have no .lnk on disk, so the
    // Start Menu scan above misses them. shell:AppsFolder mirrors both packaged and classic apps, so
    // dedupe by display name against what was already indexed; only genuinely-new names survive.
    private static void AppendAppsFolderApps(List<Entry> entries, HashSet<string> seenTitles)
    {
        List<AppsFolderEnumerator.AppEntry> apps;
        try
        {
            apps = AppsFolderEnumerator.Enumerate();
        }
        catch (Exception ex)
        {
            Log.Warning($"AppsFolder app enumeration failed: {ex.Message}");
            return;
        }

        foreach (var app in apps)
        {
            if (string.IsNullOrWhiteSpace(app.Name) || !seenTitles.Add(app.Name))
            {
                continue;
            }

            var aumid = app.Aumid;

            // A classic entry can expose a real file path instead of an AUMID; launch it directly.
            var looksLikePath = aumid.Length > 2 && aumid[1] == ':';
            var launchTarget = looksLikePath ? aumid : $"shell:AppsFolder\\{aumid}";
            var iconPath = looksLikePath ? aumid : launchTarget;
            entries.Add(new Entry(app.Name, launchTarget, iconPath));
        }
    }

    public IEnumerable<InstantResult> Query(string query)
    {
        if (!_loaded)
        {
            yield break;
        }

        IReadOnlyList<Entry> entries;
        lock (_gate)
        {
            entries = _entries;
        }

        var matches = new List<(Entry Entry, int Start)>();
        foreach (var entry in entries)
        {
            if (!Sox.Core.SearchIndex.FuzzyMatcher.IsMatch(query, entry.Title))
            {
                continue;
            }

            matches.Add((entry, PrefixStart(entry.Title, query)));
        }

        // Prefer names the query prefixes, then shorter names; a stable, cheap order for the top few.
        matches.Sort(static (a, b) =>
        {
            var c = a.Start.CompareTo(b.Start);
            return c != 0 ? c : a.Entry.Title.Length.CompareTo(b.Entry.Title.Length);
        });

        foreach (var (entry, _) in matches.Take(MaxResults))
        {
            yield return new InstantResult
            {
                Id = "app:" + entry.LaunchTarget,
                Title = entry.Title,
                LaunchTarget = entry.LaunchTarget,
                IconPath = entry.IconPath,
            };
        }
    }

    private static int PrefixStart(string title, string query)
    {
        if (title.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var idx = title.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? idx : int.MaxValue;
    }

    public void Dispose()
    {
    }
}
