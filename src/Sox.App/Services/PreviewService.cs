namespace Sox.App.Services;

internal enum PreviewKind
{
    None,
    Image,
    Svg,
    Text,
}

internal sealed class PreviewContent
{
    public PreviewKind Kind { get; init; }

    /// <summary>Raw image bytes (raster) or SVG markup.</summary>
    public byte[]? Bytes { get; init; }

    /// <summary>Text content for the text/code viewer.</summary>
    public string? Text { get; init; }

    /// <summary>Language key used to pick the highlighter (e.g. "json", "cs").</summary>
    public string? Language { get; init; }

    /// <summary>
    /// Highlight tokens, computed on the background thread during <see cref="PreviewService.LoadAsync"/>
    /// and with adjacent same-kind tokens merged. Building the runs from these on the UI thread is then
    /// cheap even while the user scrolls fast through large files.
    /// </summary>
    public IReadOnlyList<CodeToken>? Tokens { get; init; }

    public static readonly PreviewContent None = new() { Kind = PreviewKind.None };
}

internal static class PreviewService
{
    private const long MaxTextBytes = 512 * 1024;
    private const int MaxTextChars = 60_000;
    private const int MaxPreviewLines = 400;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico", ".heic", ".avif",
    };

    private static readonly Dictionary<string, string> CodeLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        [".json"] = "json",
        [".jsonc"] = "json",
        [".xml"] = "xml",
        [".xaml"] = "xml",
        [".html"] = "xml",
        [".htm"] = "xml",
        [".svg"] = "xml",
        [".cs"] = "cs",
        [".js"] = "js",
        [".ts"] = "js",
        [".tsx"] = "js",
        [".jsx"] = "js",
        [".py"] = "py",
        [".java"] = "cs",
        [".cpp"] = "cs",
        [".c"] = "cs",
        [".h"] = "cs",
        [".hpp"] = "cs",
        [".go"] = "cs",
        [".rs"] = "cs",
        [".md"] = "md",
        [".txt"] = "text",
        [".log"] = "text",
        [".yml"] = "yaml",
        [".yaml"] = "yaml",
        [".toml"] = "yaml",
        [".ini"] = "yaml",
        [".cfg"] = "yaml",
        [".ps1"] = "cs",
        [".sh"] = "sh",
        [".bat"] = "text",
        [".cmd"] = "text",
        [".css"] = "css",
        [".sql"] = "cs",
    };

    public static Task<PreviewContent> LoadAsync(string path, bool isDir)
    {
        return Task.Run(() => Load(path, isDir));
    }

    private static PreviewContent Load(string path, bool isDir)
    {
        try
        {
            if (isDir || !File.Exists(path))
            {
                return PreviewContent.None;
            }

            var ext = Path.GetExtension(path);

            if (ext.Equals(".svg", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = File.ReadAllBytes(path);
                return new PreviewContent { Kind = PreviewKind.Svg, Bytes = bytes, Language = "xml" };
            }

            if (ImageExtensions.Contains(ext))
            {
                return new PreviewContent { Kind = PreviewKind.Image, Bytes = File.ReadAllBytes(path) };
            }

            if (CodeLanguages.TryGetValue(ext, out var language))
            {
                return LoadText(path, language);
            }

            return PreviewContent.None;
        }
        catch (Exception ex)
        {
            Log.Error($"Preview failed for '{path}'", ex);
            return PreviewContent.None;
        }
    }

    private static PreviewContent LoadText(string path, string language)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxTextBytes)
        {
            return PreviewContent.None;
        }

        var text = File.ReadAllText(path);

        // Cap by characters first (cheap), then by lines so the run list handed to the UI stays small.
        if (text.Length > MaxTextChars)
        {
            text = text[..MaxTextChars];
        }

        text = TruncateLines(text, MaxPreviewLines);

        if (LooksBinary(text))
        {
            return PreviewContent.None;
        }

        // Highlighting + token merging happen here, off the UI thread.
        var tokens = CollapseAdjacent(CodeHighlighter.Highlight(text, language));

        return new PreviewContent
        {
            Kind = PreviewKind.Text,
            Text = text,
            Language = language,
            Tokens = tokens,
        };
    }

    private static string TruncateLines(string text, int maxLines)
    {
        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
            {
                continue;
            }

            if (++count >= maxLines)
            {
                return text[..(i + 1)];
            }
        }

        return text;
    }

    // Merge runs of the same kind: fewer inline elements means far cheaper layout on the UI thread.
    private static IReadOnlyList<CodeToken> CollapseAdjacent(IReadOnlyList<CodeToken> tokens)
    {
        if (tokens.Count == 0)
        {
            return tokens;
        }

        var collapsed = new List<CodeToken>(tokens.Count);
        var current = tokens[0];
        for (var i = 1; i < tokens.Count; i++)
        {
            var next = tokens[i];
            if (next.Kind == current.Kind)
            {
                current = new CodeToken(current.Text + next.Text, current.Kind);
            }
            else
            {
                collapsed.Add(current);
                current = next;
            }
        }

        collapsed.Add(current);
        return collapsed;
    }

    private static bool LooksBinary(string text)
    {
        var sample = Math.Min(text.Length, 4000);
        for (var i = 0; i < sample; i++)
        {
            if (text[i] == '\0')
            {
                return true;
            }
        }

        return false;
    }
}