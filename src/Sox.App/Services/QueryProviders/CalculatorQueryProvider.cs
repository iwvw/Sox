using System.Globalization;
using System.Text.RegularExpressions;

namespace Sox.App.Services.QueryProviders;

/// <summary>
/// Evaluates arithmetic/trig/log expressions and base conversions typed into the box, offering the
/// result to copy on Enter. Ported from Lertaro's CalculatorInstantProvider; the parser it drives is
/// <see cref="ScientificMathParser"/> (copied verbatim).
/// </summary>
internal sealed class CalculatorQueryProvider : IQueryProvider
{
    private static readonly Regex BaseConvRegex = new(
        @"^\s*(?<val>0[xX][0-9a-fA-F]+|0[bB][01]+|\d+)\s+to\s+(?<base>hex|bin|oct|dec)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex MathQueryRegex = new(
        @"^[a-zA-Z0-9+\-*/%^(),.\sπ]+$",
        RegexOptions.CultureInvariant);

    public IEnumerable<InstantResult> Query(string query)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0)
        {
            yield break;
        }

        var baseResult = TryBaseConversion(trimmed);
        if (baseResult is not null)
        {
            yield return baseResult;
            yield break;
        }

        if (!MathQueryRegex.IsMatch(trimmed))
        {
            yield break;
        }

        // Require a digit or a known constant, so a bare word ("excel") is never read as an expression.
        var hasNumber = trimmed.Any(char.IsDigit);
        var hasConstant = trimmed.Contains("pi", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("e", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains('π');
        if (!hasNumber && !hasConstant)
        {
            yield break;
        }

        double value;
        try
        {
            value = new ScientificMathParser(trimmed).Parse();
        }
        catch
        {
            // A half-typed expression throws; offering nothing is correct while the user is mid-keystroke.
            yield break;
        }

        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            yield break;
        }

        var formatted = Format(value);
        if (formatted == trimmed)
        {
            yield break;
        }

        yield return new InstantResult
        {
            Id = "calc:" + trimmed,
            Title = formatted,
            Description = "计算结果（回车复制）",
            Glyph = "\uE8EF",
            LaunchTarget = formatted,
            Action = InstantAction.Copy,
        };
    }

    private static InstantResult? TryBaseConversion(string query)
    {
        var match = BaseConvRegex.Match(query);
        if (!match.Success)
        {
            return null;
        }

        var raw = match.Groups["val"].Value;
        long value;
        try
        {
            if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                value = Convert.ToInt64(raw[2..], 16);
            }
            else if (raw.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
            {
                value = Convert.ToInt64(raw[2..], 2);
            }
            else
            {
                value = long.Parse(raw, CultureInfo.InvariantCulture);
            }
        }
        catch
        {
            return null;
        }

        var result = match.Groups["base"].Value.ToLowerInvariant() switch
        {
            "hex" => "0x" + value.ToString("X", CultureInfo.InvariantCulture),
            "bin" => "0b" + Convert.ToString(value, 2),
            "oct" => Convert.ToString(value, 8),
            _ => value.ToString(CultureInfo.InvariantCulture),
        };

        return new InstantResult
        {
            Id = "calc:" + query,
            Title = result,
            Description = "进制转换（回车复制）",
            Glyph = "\uE8EF",
            LaunchTarget = result,
            Action = InstantAction.Copy,
        };
    }

    private static string Format(double value)
    {
        if (value == Math.Floor(value) && Math.Abs(value) < 1e15)
        {
            return value.ToString("F0", CultureInfo.InvariantCulture);
        }

        return Math.Round(value, 10).ToString(CultureInfo.InvariantCulture);
    }
}
