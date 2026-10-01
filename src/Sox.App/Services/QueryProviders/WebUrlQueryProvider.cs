using System.Text.RegularExpressions;

namespace Sox.App.Services.QueryProviders;

/// <summary>
/// Recognizes explicit web addresses and protocol-less domains typed or pasted into the box and offers
/// to open them. Pure URL detection, no configuration. Ported from Lertaro's WebUrlInstantProvider.
/// </summary>
internal sealed class WebUrlQueryProvider : IQueryProvider
{
    private const string UserInfoToken = @"(?:[A-Za-z0-9._~!$&'()*+,;=:-]|%[0-9A-Fa-f]{2})";
    private const string HostLabel = @"[\p{L}\p{M}\p{N}_-]{1,63}";
    private const string Tld = @"(?:[\p{L}]{2,63}|xn--[A-Za-z0-9-]{1,59})";
    private const string DnsHost = $"(?:{HostLabel}\\.)+{Tld}";
    private const string Ipv4 = @"(?:25[0-5]|2[0-4][0-9]|1[0-9]{2}|[1-9]?[0-9])(?:\.(?:25[0-5]|2[0-4][0-9]|1[0-9]{2}|[1-9]?[0-9])){3}";
    private const string Ipv6 = @"(?:[0-9A-Fa-f]{1,4}:){7}[0-9A-Fa-f]{1,4}|(?:[0-9A-Fa-f]{1,4}:){1,7}:|(?:[0-9A-Fa-f]{1,4}:){1,6}:[0-9A-Fa-f]{1,4}|(?:[0-9A-Fa-f]{1,4}:){1,5}(?::[0-9A-Fa-f]{1,4}){1,2}|(?:[0-9A-Fa-f]{1,4}:){1,4}(?::[0-9A-Fa-f]{1,4}){1,3}|(?:[0-9A-Fa-f]{1,4}:){1,3}(?::[0-9A-Fa-f]{1,4}){1,4}|(?:[0-9A-Fa-f]{1,4}:){1,2}(?::[0-9A-Fa-f]{1,4}){1,5}|[0-9A-Fa-f]{1,4}:(?:(?::[0-9A-Fa-f]{1,4}){1,6})|:(?:(?::[0-9A-Fa-f]{1,4}){1,7}|:)";
    private const string Host = $"(?:{DnsHost}|{Ipv4}|\\[(?:{Ipv6})\\])";
    private const string Port = @"(?:0|[1-9][0-9]{0,3}|[1-5][0-9]{4}|6[0-4][0-9]{3}|65[0-4][0-9]{2}|655[0-2][0-9]|6553[0-5])";
    private const string PathOrQueryToken = @"(?:[\p{L}\p{M}\p{N}._~!$&'()*+,;=:@/?-]|%[0-9A-Fa-f]{2})";

    private static readonly Regex BareHttpUrlPattern = new(
        $@"\A(?:{UserInfoToken}+@)?{Host}(?::{Port})?(?:/{PathOrQueryToken}*)?(?:\?{PathOrQueryToken}*)?(?:\#{PathOrQueryToken}*)?\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public IEnumerable<InstantResult> Query(string query)
    {
        var explicitUrl = TryGetExplicitUrl(query);
        if (explicitUrl is not null)
        {
            yield return new InstantResult
            {
                Id = "url:" + explicitUrl,
                Title = explicitUrl,
                Description = "打开网址",
                Glyph = "\uE774",
                LaunchTarget = explicitUrl,
            };
            yield break;
        }

        if (!TryBuildWebUrls(query, out var httpsUrl, out var httpUrl))
        {
            yield break;
        }

        yield return new InstantResult
        {
            Id = "url:" + httpsUrl,
            Title = httpsUrl,
            Description = "打开网址",
            Glyph = "\uE774",
            LaunchTarget = httpsUrl,
        };
        yield return new InstantResult
        {
            Id = "url:" + httpUrl,
            Title = httpUrl,
            Description = "打开网址",
            Glyph = "\uE774",
            LaunchTarget = httpUrl,
        };
    }

    private static string? TryGetExplicitUrl(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length < 8 || query.IndexOf(' ') >= 0)
        {
            return null;
        }

        if (Uri.TryCreate(query, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
            !string.IsNullOrEmpty(uri.Host))
        {
            return query;
        }

        return null;
    }

    private static bool TryBuildWebUrls(string? input, out string httpsUrl, out string httpUrl)
    {
        httpsUrl = string.Empty;
        httpUrl = string.Empty;
        var candidate = input?.Trim();
        if (string.IsNullOrEmpty(candidate) || candidate.IndexOf(' ') >= 0 || !BareHttpUrlPattern.IsMatch(candidate))
        {
            return false;
        }

        httpsUrl = $"https://{candidate}";
        httpUrl = $"http://{candidate}";
        return true;
    }
}
