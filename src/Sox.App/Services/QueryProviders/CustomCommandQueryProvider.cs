using System.Text;
using Sox.Core;
using Sox.PluginSdk.Services;

namespace Sox.App.Services.QueryProviders;

/// <summary>
/// User-configured commands: typing a command's keyword followed by arguments offers to run its
/// executable with those arguments substituted into the parameter template. Configuration lives in
/// <see cref="UserSettings.CustomCommands"/>; the placeholder/quoting rules mirror Lertaro's
/// CustomCommands provider (positional %s1/{1}, whole-input %s/{}, quote-aware splitting).
/// </summary>
internal sealed class CustomCommandQueryProvider : IQueryProvider
{
    public IEnumerable<InstantResult> Query(string query)
    {
        List<CustomCommandSetting> commands;
        try
        {
            commands = UserSettings.Load().CustomCommands;
        }
        catch (Exception ex)
        {
            Log.Error("Reading custom commands failed", ex);
            yield break;
        }

        foreach (var command in commands)
        {
            if (!command.Enabled || string.IsNullOrWhiteSpace(command.Keyword) || string.IsNullOrWhiteSpace(command.Path))
            {
                continue;
            }

            if (!TriggerWord.TryMatchInvoked(query, command.Keyword, out var argument))
            {
                continue;
            }

            var title = string.IsNullOrWhiteSpace(command.Title) ? command.Keyword : command.Title;
            yield return new InstantResult
            {
                Id = "cmd:" + command.Keyword,
                Title = title,
                Description = "运行命令",
                LaunchTarget = command.Path,
                IconPath = command.Path,
                Glyph = string.IsNullOrEmpty(command.Path) ? "\uE756" : string.Empty,
                Action = InstantAction.RunCommand,
                Arguments = ResolveParameter(command.Parameter, argument.Trim()),
                WorkingDirectory = command.WorkingDirectory,
                RunSilently = command.RunSilently,
                RunAsAdmin = command.RunAsAdmin,
            };
            yield break;
        }
    }

    /// <summary>
    /// Resolves the argument template for <paramref name="argument"/>: %s / {} is the whole input,
    /// %s1 / {1} are 1-based positional tokens. Values are quoted as needed (the user must not add their
    /// own quotes). Mirrors Lertaro's CommandRunner.ResolveParameter.
    /// </summary>
    public static string ResolveParameter(string template, string argument)
    {
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }

        var tokens = SplitArguments(argument);
        var builder = new StringBuilder(template.Length + argument.Length);
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];

            if (c == '%' && i + 1 < template.Length && template[i + 1] == 's')
            {
                var digitsStart = i + 2;
                var digitsEnd = digitsStart;
                while (digitsEnd < template.Length && char.IsDigit(template[digitsEnd]))
                {
                    digitsEnd++;
                }

                if (digitsEnd > digitsStart)
                {
                    var index = int.Parse(template[digitsStart..digitsEnd]) - 1;
                    builder.Append(index >= 0 && index < tokens.Count ? Quote(tokens[index]) : string.Empty);
                    i = digitsEnd - 1;
                }
                else
                {
                    builder.Append(Quote(argument));
                    i++;
                }

                continue;
            }

            if (c == '{')
            {
                var close = template.IndexOf('}', i + 1);
                if (close >= 0)
                {
                    var inner = template[(i + 1)..close];
                    if (inner.Length == 0)
                    {
                        builder.Append(Quote(argument));
                    }
                    else if (int.TryParse(inner, out var position))
                    {
                        var index = position - 1;
                        builder.Append(index >= 0 && index < tokens.Count ? Quote(tokens[index]) : string.Empty);
                    }
                    else
                    {
                        builder.Append(template, i, close - i + 1);
                    }

                    i = close;
                    continue;
                }
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    // Quote-aware split on whitespace: "a b" stays one token, quotes are stripped.
    private static List<string> SplitArguments(string argument)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var c in argument)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private static string Quote(string value)
    {
        if (value.Length > 0 && value.IndexOfAny([' ', '\t', '"']) < 0)
        {
            return value;
        }

        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
