using Sox.Core.SearchIndex;
using Xunit;

namespace Sox.Core.Tests;

// Regression guard for the "a query starting with 'x:' swallows the whole term set" bug: the file-search
// parse strips a leading drive spec into TargetDrive, which leaves zero terms for any caller that has no
// notion of drives (app names, window titles). Those callers used IsMatch and lost every candidate,
// including the ones that would legitimately match the text after the colon.
public class FuzzyMatcherDriveSpecTests
{
    [Theory]
    [InlineData("c:alc")]
    [InlineData("e:verything")]
    [InlineData("d:x")]
    public void IsMatch_DropsTextAfterAColonOnlyWhenTheWholeTermIsTheDriveSpec(string query)
    {
        // "c:alc" keeps "alc" as a term, so it still matches -- this is the documented file-search rule.
        Assert.True(FuzzyMatcher.IsMatch(query, query[2..]));
    }

    [Theory]
    [InlineData("c:")]
    [InlineData("e:")]
    [InlineData("z:")]
    public void IsMatch_YieldsNoMatchWhenTheQueryIsOnlyADriveSpec(string query)
    {
        // A bare drive spec parses to zero terms, which this seam must treat as "no match" rather than
        // "match everything" -- but it also means no candidate can ever be found through it.
        Assert.False(FuzzyMatcher.IsMatch(query, "Calculator"));
    }

    [Theory]
    [InlineData("c:")]
    [InlineData("e:")]
    [InlineData("z:")]
    public void IsMatchFreeText_StillHasATermForABareDriveSpec(string query)
    {
        // The free-text parse keeps the colon in the term, so a text that actually contains ":"
        // (a registry path, a plugin id, ...) can still be found instead of silently vanishing.
        Assert.True(FuzzyMatcher.IsMatchFreeText(query, query));
    }

    [Theory]
    [InlineData("c:", "Calculator")]
    [InlineData("e:", "everything")]
    public void IsMatchFreeText_RequiresTheColonToActuallyAppear(string query, string text)
    {
        // Keeping the colon in the term is the fix; it is not a licence to match unrelated text.
        Assert.False(FuzzyMatcher.IsMatchFreeText(query, text));
    }

    [Theory]
    [InlineData("calc", "Calculator")]
    [InlineData("eve", "everything")]
    public void IsMatchFreeText_MatchesPlainQueries(string query, string text)
    {
        Assert.True(FuzzyMatcher.IsMatchFreeText(query, text));
    }

    [Theory]
    [InlineData("zzz", "Calculator")]
    [InlineData("", "Calculator")]
    [InlineData("calc", "")]
    public void IsMatchFreeText_RejectsNonMatches(string query, string text)
    {
        Assert.False(FuzzyMatcher.IsMatchFreeText(query, text));
    }
}
