namespace Sox.App.Services.QueryProviders;

/// <summary>What a row does when activated.</summary>
internal enum InstantAction
{
    /// <summary>Launch <see cref="InstantResult.LaunchTarget"/> via the shell (path or URL).</summary>
    Open,

    /// <summary>Copy <see cref="InstantResult.LaunchTarget"/> to the clipboard (calculator results).</summary>
    Copy,

    /// <summary>Bring the window identified by <see cref="InstantResult.LaunchTarget"/> (an HWND string) forward.</summary>
    ActivateWindow,

    /// <summary>Run the custom command whose keyword is <see cref="InstantResult.LaunchTarget"/>,
    /// with the typed argument substituted into its template.</summary>
    RunCommand,
}

/// <summary>
/// One non-file result produced by a front-end query provider (an application, a web search, a
/// calculator answer, a window, a custom command). Deliberately separate from
/// <see cref="Sox.Core.SearchResult"/>: these are self-contained actions with no index row, no history
/// learning and no path-based ranking, and keeping them out of the core result type avoids touching the
/// wire protocol. See ADR-0015.
/// </summary>
internal sealed class InstantResult
{
    /// <summary>Stable identity across keystrokes, used as the row's key for keyed diffing.</summary>
    public required string Id { get; init; }

    public required string Title { get; init; }

    public string Description { get; init; } = string.Empty;

    /// <summary>Action payload: a disk path/URL for Open, the copied text for Copy, an HWND for
    /// ActivateWindow.</summary>
    public required string LaunchTarget { get; init; }

    /// <summary>Path the row's icon is taken from. Differs from the target for a .lnk, where the
    /// shortcut's own icon carries the link overlay and the target's does not.</summary>
    public string? IconPath { get; init; }

    /// <summary>Segoe Fluent glyph shown when there is no <see cref="IconPath"/> to load a shell icon
    /// from, so non-file results (URL / calculator / window / command) are still distinguishable.</summary>
    public string Glyph { get; init; } = string.Empty;

    public InstantAction Action { get; init; } = InstantAction.Open;

    /// <summary>Resolved command-line arguments, for <see cref="InstantAction.RunCommand"/>
    /// (see <see cref="CustomCommandSetting"/>).</summary>
    public string Arguments { get; init; } = string.Empty;

    /// <summary>Working directory, for <see cref="InstantAction.RunCommand"/>.</summary>
    public string WorkingDirectory { get; init; } = string.Empty;

    public bool RunSilently { get; init; }

    public bool RunAsAdmin { get; init; }
}
