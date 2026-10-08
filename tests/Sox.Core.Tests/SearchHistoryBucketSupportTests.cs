using Sox.Core;
using Sox.PluginSdk.Services;
using Xunit;

namespace Sox.Core.Tests;

// The bucket rules decide which keyword "owns" a path and how many entries a keyword keeps. A regression
// here silently changes what a typed query recalls, so the invariants are pinned explicitly.
public class SearchHistoryBucketSupportTests
{
    private static SearchHistoryStore.StoredEntry Entry(string path, long time, int count = 1)
        => new(path, HistoryEntryKind.File, time, count);

    [Fact]
    public void BuildBuckets_KeepsMostRecentOccurrenceAcrossKeywords()
    {
        var buckets = SearchHistoryBucketSupport.BuildBuckets(
        [
            ("new", Entry(@"C:\a", 200)),
            ("old", Entry(@"C:\a", 100)),
        ]);

        Assert.True(buckets.ContainsKey("new"));
        Assert.False(buckets.ContainsKey("old"));
        Assert.Single(buckets["new"]);
    }

    [Fact]
    public void BuildBuckets_NormalizesZeroCountToOne()
    {
        var buckets = SearchHistoryBucketSupport.BuildBuckets([("k", Entry(@"C:\a", 1, count: 0))]);

        Assert.Equal(1, buckets["k"][0].Count);
    }

    [Fact]
    public void BuildBuckets_DoesNotCreateEmptyBucketWhenAllCandidatesDuplicated()
    {
        var buckets = SearchHistoryBucketSupport.BuildBuckets(
        [
            ("a", Entry(@"C:\x", 200)),
            ("b", Entry(@"C:\x", 100)),
        ]);

        Assert.False(buckets.ContainsKey("b"));
    }

    [Fact]
    public void BuildBuckets_EnforcesPerKeywordCap()
    {
        var items = Enumerable.Range(0, SearchHistoryBucketSupport.MaxEntriesPerKeyword + 5)
            .Select(i => ("k", Entry($@"C:\f{i}", 1000 - i)));

        var buckets = SearchHistoryBucketSupport.BuildBuckets(items);

        Assert.Equal(SearchHistoryBucketSupport.MaxEntriesPerKeyword, buckets["k"].Count);
    }

    [Fact]
    public void Flatten_ReturnsMostRecentFirst()
    {
        var buckets = SearchHistoryBucketSupport.BuildBuckets(
        [
            ("a", Entry(@"C:\old", 100)),
            ("b", Entry(@"C:\new", 300)),
            ("c", Entry(@"C:\mid", 200)),
        ]);

        var flat = SearchHistoryBucketSupport.Flatten(buckets);

        Assert.Equal([@"C:\new", @"C:\mid", @"C:\old"], flat.Select(e => e.Path));
    }

    [Fact]
    public void BuildPriorityCache_IsCaseInsensitiveAndNonEmpty()
    {
        var buckets = SearchHistoryBucketSupport.BuildBuckets(
        [
            ("a", Entry(@"C:\Foo", 100, count: 3)),
            ("b", Entry(@"C:\bar", 100, count: 1)),
        ]);

        var priorities = SearchHistoryBucketSupport.BuildPriorityCache(buckets, nowUnix: 100);

        Assert.True(priorities.ContainsKey(@"c:\foo"));
        Assert.True(priorities[@"c:\foo"] > priorities[@"c:\bar"]);
    }
}
