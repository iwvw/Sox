using System.Buffers.Binary;

namespace Sox.Core.IndexV2.Persistence;

// CRC32C (Castagnoli). Only used to detect same-length corruption of a snapshot's section region, which
// the layout checks cannot see -- not a security primitive, so the table is built once and kept simple.
internal static class SnapshotCrc32
{
    private const uint Polynomial = 0x82F63B78;
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var crc = i;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ Polynomial : crc >> 1;
            table[i] = crc;
        }
        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> data) => Continue(0u, data);

    // `previous` is a value returned by Compute/Continue; the caller starts a fresh run with 0.
    public static uint Continue(uint previous, ReadOnlySpan<byte> data)
    {
        var crc = ~previous;
        foreach (var b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    public static uint ReadStored(Span<byte> field) => BinaryPrimitives.ReadUInt32LittleEndian(field);

    public static void WriteStored(Span<byte> field, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(field, value);
}
