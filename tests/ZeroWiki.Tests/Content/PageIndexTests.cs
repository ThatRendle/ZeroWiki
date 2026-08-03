using ZeroWiki.Content;

namespace ZeroWiki.Tests.Content;

/// <summary>Exercises <see cref="PageIndex"/>, the process-memory singleton holder (D15).</summary>
public sealed class PageIndexTests
{
    [Fact]
    public void Current_BeforeAnyReplace_IsTheEmptySnapshot()
    {
        var index = new PageIndex();

        Assert.Same(PageIndexSnapshot.Empty, index.Current);
    }

    [Fact]
    public void Replace_InstallsTheNewSnapshotWholesale()
    {
        var index = new PageIndex();
        var entry = new PageIndexEntry("route", "route.md", "/abs/route.md", "Title", ["tag"], null);
        var snapshot = new PageIndexSnapshot([entry], [], [], "deadbeef");

        index.Replace(snapshot);

        Assert.Same(snapshot, index.Current);
    }
}
