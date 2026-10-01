namespace Sox.App.Services;

internal enum TokenKind
{
    Plain,
    Keyword,
    String,
    Comment,
    Number,
    Type,
    Tag,
    Attribute,
    Punctuation,
}

internal readonly record struct CodeToken(string Text, TokenKind Kind);

internal static class CodeHighlighter
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "async", "await", "base", "bool", "break", "byte", "case", "catch", "char",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else", "enum",
        "event", "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "goto",
        "if", "implicit", "in", "int", "interface", "internal", "is", "lock", "long", "namespace", "new",
        "null", "object", "operator", "out", "override", "params", "private", "protected", "public",
        "readonly", "record", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc",
        "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong",
        "unchecked", "unsafe", "ushort", "using", "var", "virtual", "void", "volatile", "while", "yield",
        "def", "elif", "except", "from", "global", "import", "lambda", "None", "nonlocal", "not", "or",
        "and", "pass", "raise", "with", "True", "False", "self", "function", "let", "const", "export",
        "default", "extends", "super", "typeof", "instanceof", "delete", "function", "func", "package",
        "type", "interface", "map", "chan", "go", "defer", "range", "fn", "impl", "trait", "match",
        "mut", "pub", "use", "mod", "let", "struct", "enum",
    };

    public static IReadOnlyList<CodeToken> Highlight(string text, string language)
    {
        return language switch
        {
            "json" => HighlightJson(text),
            "xml" => HighlightXml(text),
            "md" => HighlightMarkdown(text),
            "yaml" => HighlightYaml(text),
            "css" => HighlightCss(text),
            "sh" => HighlightShell(text),
            "js" or "cs" or "py" => HighlightCode(text),
            _ => new[] { new CodeToken(text, TokenKind.Plain) },
        };
    }

    private static List<CodeToken> HighlightCode(string text)
    {
        var tokens = new List<CodeToken>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];

            // Line comment
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                var end = text.IndexOf('\n', i);
                if (end < 0)
                {
                    end = text.Length;
                }

                tokens.Add(new CodeToken(text[i..end], TokenKind.Comment));
                i = end;
                continue;
            }

            // Hash comment (python/shell)
            if (c == '#')
            {
                var end = text.IndexOf('\n', i);
                if (end < 0)
                {
                    end = text.Length;
                }

                tokens.Add(new CodeToken(text[i..end], TokenKind.Comment));
                i = end;
                continue;
            }

            // Block comment
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + 2;
                tokens.Add(new CodeToken(text[i..end], TokenKind.Comment));
                i = end;
                continue;
            }

            // String
            if (c == '"' || c == '\'' || c == '`')
            {
                var end = ScanString(text, i, c);
                tokens.Add(new CodeToken(text[i..end], TokenKind.String));
                i = end;
                continue;
            }

            // Number
            if (char.IsDigit(c))
            {
                var end = i;
                while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '.' || text[end] == '_'))
                {
                    end++;
                }

                tokens.Add(new CodeToken(text[i..end], TokenKind.Number));
                i = end;
                continue;
            }

            // Identifier / keyword
            if (char.IsLetter(c) || c == '_')
            {
                var end = i;
                while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_'))
                {
                    end++;
                }

                var word = text[i..end];
                tokens.Add(new CodeToken(word, Keywords.Contains(word) ? TokenKind.Keyword : TokenKind.Plain));
                i = end;
                continue;
            }

            tokens.Add(new CodeToken(c.ToString(), TokenKind.Plain));
            i++;
        }

        return tokens;
    }

    private static List<CodeToken> HighlightJson(string text) => HighlightCode(text);

    private static List<CodeToken> HighlightXml(string text)
    {
        var tokens = new List<CodeToken>();
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '<')
            {
                var end = text.IndexOf('>', i);
                if (end < 0)
                {
                    end = text.Length - 1;
                }

                var tag = text[i..(end + 1)];
                tokens.Add(new CodeToken(tag, TokenKind.Tag));
                i = end + 1;
                continue;
            }

            var next = text.IndexOf('<', i);
            if (next < 0)
            {
                next = text.Length;
            }

            tokens.Add(new CodeToken(text[i..next], TokenKind.Plain));
            i = next;
        }

        return tokens;
    }

    private static List<CodeToken> HighlightMarkdown(string text)
    {
        var tokens = new List<CodeToken>();
        foreach (var line in SplitLines(text))
        {
            if (line.StartsWith('#'))
            {
                tokens.Add(new CodeToken(line, TokenKind.Keyword));
            }
            else if (line.TrimStart().StartsWith("- ") || line.TrimStart().StartsWith("* "))
            {
                tokens.Add(new CodeToken(line, TokenKind.Plain));
            }
            else
            {
                tokens.Add(new CodeToken(line, TokenKind.Plain));
            }
        }

        return tokens;
    }

    private static List<CodeToken> HighlightYaml(string text)
    {
        var tokens = new List<CodeToken>();
        foreach (var line in SplitLines(text))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#'))
            {
                tokens.Add(new CodeToken(line, TokenKind.Comment));
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                tokens.Add(new CodeToken(line[..(colon + 1)], TokenKind.Attribute));
                tokens.Add(new CodeToken(line[(colon + 1)..], TokenKind.Plain));
            }
            else
            {
                tokens.Add(new CodeToken(line, TokenKind.Plain));
            }
        }

        return tokens;
    }

    private static List<CodeToken> HighlightCss(string text) => HighlightCode(text);

    private static List<CodeToken> HighlightShell(string text) => HighlightCode(text);

    private static IEnumerable<string> SplitLines(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                yield return text[start..(i + 1)];
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            yield return text[start..];
        }
    }

    private static int ScanString(string text, int start, char quote)
    {
        var i = start + 1;
        while (i < text.Length)
        {
            if (text[i] == '\\')
            {
                i += 2;
                continue;
            }

            if (text[i] == quote)
            {
                return i + 1;
            }

            if (text[i] == '\n' && quote != '`')
            {
                return i;
            }

            i++;
        }

        return text.Length;
    }
}
