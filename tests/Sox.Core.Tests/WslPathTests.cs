using Sox.Core;
using Xunit;

namespace Sox.Core.Tests;

public class WslPathTests
{
    [Theory]
    [InlineData(@"\\wsl$\Ubuntu\home\me")]
    [InlineData(@"\\wsl.localhost\Ubuntu\home\me")]
    [InlineData(@"\\WSL$\Ubuntu")]
    public void IsPath_RecognizesWslUncPrefixes(string path)
    {
        Assert.True(WslPath.IsPath(path));
    }

    [Theory]
    [InlineData(@"C:\Users\me")]
    [InlineData(@"\\server\share")]
    [InlineData("")]
    [InlineData(null)]
    public void IsPath_RejectsNonWslPaths(string? path)
    {
        Assert.False(WslPath.IsPath(path));
    }
}
