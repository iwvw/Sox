using Sox.Core.IndexV2.Persistence;
using Xunit;

namespace Sox.Core.Tests;

// The snapshot header now carries a CRC32C over the section region. These pin the algorithm itself and the
// field position the writer seeks back to, since a wrong offset would write the checksum into a count.
public class SnapshotCrc32Tests
{
    [Fact]
    public void Compute_IsStableForKnownInput()
    {
        // CRC32C of "123456789" is the standard test vector 0xE3069283.
        var crc = SnapshotCrc32.Compute("123456789"u8);
        Assert.Equal(0xE3069283u, crc);
    }

    [Fact]
    public void Compute_EmptyInputIsZero()
    {
        Assert.Equal(0u, SnapshotCrc32.Compute(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Continue_MatchesComputeOverTheConcatenation()
    {
        var whole = "the quick brown fox jumps over the lazy dog"u8.ToArray();
        var split = 17;

        var first = SnapshotCrc32.Continue(0u, whole.AsSpan(0, split));
        var second = SnapshotCrc32.Continue(first, whole.AsSpan(split));

        Assert.Equal(SnapshotCrc32.Compute(whole), second);
    }

    [Fact]
    public void Compute_DetectsSingleBitFlip()
    {
        var data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var original = SnapshotCrc32.Compute(data);

        data[4] ^= 0x10;

        Assert.NotEqual(original, SnapshotCrc32.Compute(data));
    }

    [Fact]
    public void CrcFieldOffset_IsStableAndInsideTheHeader()
    {
        var meta = new SnapshotFormat.Meta { SourceKey = "test" };
        var offset = SnapshotFormat.GetSectionsCrcFieldOffset(meta);

        // Magic(8) + Version(4) + ten ints(40) + two bytes(2) + uint(4) = 58.
        Assert.Equal(58, offset);
    }
}
