using Sox.Core.IndexV2.Persistence;
using Xunit;

namespace Sox.Core.Tests;

// The section layout is the single source of truth shared by the writer and the reader's unsafe span
// accessors: if offsets stop being 16-aligned or monotonic, typed spans over the mmap are misaligned
// (crash) or overlap. These pin the layout invariants the reader relies on.
public class SnapshotFormatTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 16)]
    [InlineData(15, 16)]
    [InlineData(16, 16)]
    [InlineData(17, 32)]
    public void Align_RoundsUpToSectionAlignment(long input, long expected)
    {
        Assert.Equal(expected, SnapshotFormat.Align(input));
    }

    [Fact]
    public void ComputeSectionOffsets_AreAlignedAndMonotonic()
    {
        var meta = new SnapshotFormat.Meta
        {
            SourceKey = "test",
            SectionsOffset = SnapshotFormat.Align(64),
            RowCount = 3,
            UniqueCount = 2,
            NameBlobLength = 10,
            ChildrenLength = 4,
            AliasEntryCount = 1,
            AliasBlobLength = 3,
            OrphanCount = 1,
        };

        var offsets = SnapshotFormat.ComputeSectionOffsets(meta, out var totalLength);

        for (var i = 0; i < offsets.Length; i++)
        {
            Assert.Equal(0, offsets[i] % SnapshotFormat.SectionAlignment);
            if (i > 0)
            {
                Assert.True(offsets[i] >= offsets[i - 1]);
            }
        }

        Assert.True(totalLength >= offsets[^1]);
        Assert.Equal(0, totalLength % SnapshotFormat.SectionAlignment);
    }

    [Fact]
    public void ComputeSectionOffsets_RejectsNegativeCounts()
    {
        var meta = new SnapshotFormat.Meta { SourceKey = "bad", RowCount = -1 };

        Assert.Throws<InvalidDataException>(() => SnapshotFormat.ComputeSectionOffsets(meta, out _));
    }

    [Fact]
    public void ComputeSectionOffsets_EmptySnapshotHasNoSectionsBeyondHeader()
    {
        var meta = new SnapshotFormat.Meta { SourceKey = "empty" };

        var offsets = SnapshotFormat.ComputeSectionOffsets(meta, out var totalLength);

        // Only the trailing (UniqueCount + 1) and (RowCount + 1) sentinel columns have any size, and all
        // sections stay aligned within the reported total.
        Assert.All(offsets, o => Assert.True(o <= totalLength));
    }
}
