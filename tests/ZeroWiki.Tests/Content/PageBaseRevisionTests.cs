using ZeroWiki.Content;

namespace ZeroWiki.Tests.Content;

public sealed class PageBaseRevisionTests
{
    [Fact]
    public void AbsentAtHead_HasNoBlobSha()
    {
        Assert.Null(PageBaseRevision.AbsentAtHead.BlobSha);
    }

    [Fact]
    public void ForBlob_StoresTheGivenSha()
    {
        var revision = PageBaseRevision.ForBlob("deadbeef");

        Assert.Equal("deadbeef", revision.BlobSha);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ForBlob_RejectsAnEmptyOrWhitespaceSha(string value)
    {
        Assert.Throws<ArgumentException>(() => PageBaseRevision.ForBlob(value));
    }
}
