using Sox.Core;
using Sox.Core.Wire;
using Xunit;

namespace Sox.Core.Tests;

// The pipe protocol has no negotiation: the App and the service must agree byte for byte, and a mismatch
// shows up as "search returns nothing" rather than a clear error. These pin the round trip.
public class WireCodecTests
{
    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("Sox")]
    [InlineData("C:\\Users\\DSUK\\文档\\报告.docx")]
    [InlineData("日本語のファイル名.txt")]
    public void VarIntString_RoundTrips(string value)
    {
        var buffer = new byte[512];
        var writeOffset = 0;
        VarIntStringCodec.WriteString(buffer, ref writeOffset, value);

        var readOffset = 0;
        var decoded = VarIntStringCodec.ReadString(buffer, ref readOffset);

        Assert.Equal(value, decoded);
        Assert.Equal(writeOffset, readOffset);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(16383)]
    [InlineData(int.MaxValue)]
    public void SevenBitEncodedInt_RoundTrips(int value)
    {
        var buffer = new byte[8];
        var writeOffset = 0;
        VarIntStringCodec.Write7BitEncodedInt(buffer, ref writeOffset, value);

        var readOffset = 0;
        var decoded = VarIntStringCodec.Read7BitEncodedInt(buffer, ref readOffset);

        Assert.Equal(value, decoded);
        Assert.Equal(writeOffset, readOffset);
    }

    [Fact]
    public void SearchResultFrame_RoundTrips()
    {
        var result = new SearchResult
        {
            Name = "Sox.exe",
            Path = @"C:\Program Files\Sox\Sox.exe",
            IsDir = false,
            Drive = "C",
            Attributes = FileAttributes.Archive,
        };

        var payload = new byte[SearchResultFrameCodec.MaxPayloadSize(result)];
        var written = SearchResultFrameCodec.WritePayload(payload, result);
        var decoded = SearchResultFrameCodec.ReadPayload(payload[..written]);

        Assert.Equal(result.Name, decoded.Name);
        Assert.Equal(result.Path, decoded.Path);
        Assert.Equal(result.IsDir, decoded.IsDir);
        Assert.Equal(result.Drive, decoded.Drive);
    }

    [Fact]
    public void SearchResultFrame_RoundTripsDirectory()
    {
        var result = new SearchResult
        {
            Name = "sox",
            Path = @"E:\Code\sox",
            IsDir = true,
            Drive = "E",
            Attributes = FileAttributes.Directory,
        };

        var payload = new byte[SearchResultFrameCodec.MaxPayloadSize(result)];
        var written = SearchResultFrameCodec.WritePayload(payload, result);
        var decoded = SearchResultFrameCodec.ReadPayload(payload[..written]);

        Assert.True(decoded.IsDir);
        Assert.Equal(result.Path, decoded.Path);
    }

    [Fact]
    public void SearchResultFrame_TruncatedPayloadDoesNotThrow()
    {
        var result = new SearchResult
        {
            Name = "averylongfilename.txt",
            Path = @"C:\some\deep\path\averylongfilename.txt",
            IsDir = false,
            Drive = "C",
            Attributes = FileAttributes.Archive,
        };

        var payload = new byte[SearchResultFrameCodec.MaxPayloadSize(result)];
        var written = SearchResultFrameCodec.WritePayload(payload, result);

        for (var cut = 1; cut < written; cut++)
        {
            var truncated = payload[..cut];
            var decoded = SearchResultFrameCodec.ReadPayload(truncated);
            Assert.NotNull(decoded);
        }
    }
}
