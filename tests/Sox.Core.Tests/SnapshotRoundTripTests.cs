using Sox.Core;
using Sox.Core.IndexV2.Persistence;
using Xunit;

namespace Sox.Core.Tests;

// End-to-end snapshot round trip. The unit tests around SnapshotFormat only exercise the layout
// arithmetic; this one actually writes a file through SnapshotWriter and reads it back through
// Snapshot.Open, which is what catches stream-access and checksum-wiring mistakes (a write-only
// FileStream made the CRC read-back throw, and that shipped because nothing wrote a real snapshot).
public class SnapshotRoundTripTests : IDisposable
{
    private readonly string _dir;

    public SnapshotRoundTripTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sox-snap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static FileRecordStore SampleStore()
    {
        var store = new FileRecordStore
        {
            SourceKey = "C",
            SourceKind = FileRecordSourceKind.LocalMft,
            IdKind = FileRecordIdKind.MftFrn,
            FileSystemType = "NTFS",
            RootId = new UInt128(0, 1),
            IsComplete = true,
        };

        store.Records.Add(new FileRecord(new UInt128(0, 1), new UInt128(0, 1), "C:",
            FileRecordFlags.Directory | FileRecordFlags.SourceRoot));
        store.Records.Add(new FileRecord(new UInt128(0, 2), new UInt128(0, 1), "sub",
            FileRecordFlags.Directory));
        store.Records.Add(new FileRecord(new UInt128(0, 3), new UInt128(0, 2), "a.txt",
            FileRecordFlags.None, size: 42));
        store.Records.Add(new FileRecord(new UInt128(0, 4), new UInt128(0, 1), "b.txt",
            FileRecordFlags.None, size: 7));

        return store;
    }

    [Fact]
    public void Write_ThenOpenWithChecksum_RoundTrips()
    {
        var path = Path.Combine(_dir, "snap.idx");

        SnapshotWriter.Write(SampleStore(), path);

        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 0);

        using var snapshot = Snapshot.Open(path, verifyChecksum: true);

        Assert.Equal(4, snapshot.Count);
        Assert.Equal(2, snapshot.TotalFiles);
        Assert.Equal(2, snapshot.TotalDirs);
    }

    [Fact]
    public void Write_LargeStore_RoundTripsAndChecksumsAcrossBuffers()
    {
        var store = new FileRecordStore
        {
            SourceKey = "C",
            SourceKind = FileRecordSourceKind.LocalMft,
            IdKind = FileRecordIdKind.MftFrn,
            FileSystemType = "NTFS",
            RootId = new UInt128(0, 1),
            IsComplete = true,
        };
        store.Records.Add(new FileRecord(new UInt128(0, 1), new UInt128(0, 1), "C:",
            FileRecordFlags.Directory | FileRecordFlags.SourceRoot));
        for (var i = 2; i < 5000; i++)
        {
            store.Records.Add(new FileRecord(new UInt128(0, (ulong)i), new UInt128(0, 1), $"file-{i:D4}.txt",
                FileRecordFlags.None, size: i));
        }

        var path = Path.Combine(_dir, "large.idx");
        SnapshotWriter.Write(store, path);

        using var snapshot = Snapshot.Open(path, verifyChecksum: true);

        Assert.Equal(4999, snapshot.Count);
        Assert.Equal(4998, snapshot.TotalFiles);
        Assert.Equal(1, snapshot.TotalDirs);
    }

    [Fact]
    public void RoundTrip_PreservesNamesFlagsAndPaths()
    {
        var path = Path.Combine(_dir, "snap.idx");
        SnapshotWriter.Write(SampleStore(), path);

        using var snapshot = Snapshot.Open(path, verifyChecksum: true);

        var fileRow = snapshot.FirstRowForId(new UInt128(0, 3));
        Assert.True(fileRow >= 0);
        Assert.Equal("a.txt", snapshot.GetName(fileRow));
        Assert.False(snapshot.IsDirectory(fileRow));

        // Path building walks the parent chain; assert on the tail so the test does not pin the
        // SourceRoot/"root row name" convention.
        Assert.EndsWith(@"sub\a.txt", snapshot.GetFullPath(fileRow));

        var dirRow = snapshot.FirstRowForId(new UInt128(0, 2));
        Assert.True(dirRow >= 0);
        Assert.True(snapshot.IsDirectory(dirRow));
    }

    [Fact]
    public void Open_RejectsCorruptedSectionBytes()
    {
        var path = Path.Combine(_dir, "snap.idx");
        SnapshotWriter.Write(SampleStore(), path);

        // Flip a byte well inside the section region (past the header) and the checksum must reject it,
        // which is the whole point of stamping one: same-length corruption passes every layout check.
        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        Assert.ThrowsAny<InvalidDataException>(() => Snapshot.Open(path, verifyChecksum: true));
    }

    // Cache discovery scans every .idx through this path. It reads magic/version itself and then reads
    // the body, so a copy that re-read magic before delegating to ReadHeader (which reads it again)
    // rejected every VALID snapshot with "magic mismatch" while silently accepting invalid ones -- the
    // exact inversion that made the drive-maintenance poller log three errors every cycle.
    [Fact]
    public void TryReadHeaderFromFile_ReadsAFreshlyWrittenSnapshot()
    {
        var path = Path.Combine(_dir, "snap.idx");
        SnapshotWriter.Write(SampleStore(), path);

        var meta = SnapshotFormat.TryReadHeaderFromFile(path);

        Assert.NotNull(meta);
        Assert.Equal(4, meta!.RowCount);
        Assert.Equal("C", meta.SourceKey);
    }

    [Fact]
    public void TryReadHeaderFromFile_ReturnsNullForForeignVersion()
    {
        var path = Path.Combine(_dir, "snap.idx");
        SnapshotWriter.Write(SampleStore(), path);

        var bytes = File.ReadAllBytes(path);
        BitConverter.GetBytes(SnapshotFormat.Version + 1).CopyTo(bytes, 8);
        File.WriteAllBytes(path, bytes);

        Assert.Null(SnapshotFormat.TryReadHeaderFromFile(path));
    }

    [Fact]
    public void TryReadHeaderFromFile_ReturnsNullForTooShortFile()
    {
        var path = Path.Combine(_dir, "tiny.idx");
        File.WriteAllBytes(path, new byte[8]);

        Assert.Null(SnapshotFormat.TryReadHeaderFromFile(path));
    }
}
