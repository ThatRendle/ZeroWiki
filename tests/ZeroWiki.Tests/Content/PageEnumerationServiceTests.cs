using Microsoft.Extensions.Logging.Abstractions;
using ZeroWiki.Content;

namespace ZeroWiki.Tests.Content;

/// <summary>
/// Exercises <see cref="PageEnumerationService"/> against a throwaway temp directory standing in for
/// <see cref="ContentPaths.WorkingTree"/> — real files on a real filesystem, not a fake.
/// </summary>
public sealed class PageEnumerationServiceTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"zerowiki-page-enum-{Guid.NewGuid():n}");

    private ContentPaths Paths => new(_dataRoot);

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
    }

    private PageEnumerationService CreateService() =>
        new(Paths, NullLogger<PageEnumerationService>.Instance);

    private void WriteFile(string relativePath, string content = "content")
    {
        var full = Path.Combine(Paths.WorkingTree, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public void MissingWorkingTree_ReturnsEmptyResultRatherThanThrowing()
    {
        var result = CreateService().EnumeratePages();

        Assert.Empty(result.Pages);
        Assert.Empty(result.AmbiguousRoutes);
        Assert.Empty(result.UnreadableDirectories);
    }

    [Fact]
    public void NestedPage_IsAddressableByItsPath()
    {
        WriteFile(Path.Combine("Project Notes", "Kick Off.md"));

        var result = CreateService().EnumeratePages();

        var page = Assert.Single(result.Pages);
        Assert.Equal("Project_Notes/Kick_Off", page.Route);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(Paths.WorkingTree, "Project Notes", "Kick Off.md")),
            Path.GetFullPath(page.AbsolutePath));
    }

    [Fact]
    public void NonMarkdownFiles_AreNotEnumerated()
    {
        WriteFile("image.png");
        WriteFile("notes.txt");
        WriteFile("page.md");

        var result = CreateService().EnumeratePages();

        var page = Assert.Single(result.Pages);
        Assert.Equal("page", page.Route);
    }

    [Fact]
    public void DotPrefixedFilesAndDirectories_AreSkipped()
    {
        WriteFile(".obsidian/workspace.md");
        WriteFile(".hidden-page.md");
        WriteFile("visible.md");

        var result = CreateService().EnumeratePages();

        var page = Assert.Single(result.Pages);
        Assert.Equal("visible", page.Route);
    }

    [Fact]
    public void AmbiguousRoute_IsRefusedNotGuessed_AndOtherPagesAreUnaffected()
    {
        // D12's canonical fixture: "a_ b.md" and "a _b.md" both encode to "a___b".
        WriteFile("a_ b.md");
        WriteFile("a _b.md");
        WriteFile("unaffected.md");

        var result = CreateService().EnumeratePages();

        Assert.DoesNotContain(result.Pages, p => p.Route == "a___b");
        var ambiguous = Assert.Single(result.AmbiguousRoutes);
        Assert.Equal("a___b", ambiguous.Route);
        Assert.Equal(["a _b.md", "a_ b.md"], ambiguous.RelativePaths);

        var unaffected = Assert.Single(result.Pages);
        Assert.Equal("unaffected", unaffected.Route);
    }

    [Fact]
    public void SoleClaimantWhoseRouteDoesNotIdentifyIt_IsRefused()
    {
        // The reviewer's fixture: "Chapter  1.md" (two spaces) is the tree's ONLY file, so grouping by
        // claimant count alone sees no collision at all — yet its route "Chapter__1" decodes back to
        // "Chapter_1.md", a file that does not exist. Claimant-count checking would serve this page and
        // let a later save write into a phantom "Chapter_1.md" instead of the file being read.
        WriteFile("Chapter  1.md");
        WriteFile("unaffected.md");

        var result = CreateService().EnumeratePages();

        Assert.DoesNotContain(result.Pages, p => p.Route == "Chapter__1");
        var refused = Assert.Single(result.AmbiguousRoutes);
        Assert.Equal("Chapter__1", refused.Route);
        Assert.Equal(["Chapter  1.md"], refused.RelativePaths);

        var unaffected = Assert.Single(result.Pages);
        Assert.Equal("unaffected", unaffected.Route);
    }

    [Fact]
    public void SoleClaimantWhoseRouteDoesIdentifyIt_IsServedNormally()
    {
        // The companion positive case: an ordinary single space round-trips cleanly, so a ordinary page
        // is unaffected by the stricter per-route check.
        WriteFile("Chapter 1.md");

        var result = CreateService().EnumeratePages();

        Assert.Empty(result.AmbiguousRoutes);
        var page = Assert.Single(result.Pages);
        Assert.Equal("Chapter_1", page.Route);
    }

    [Fact]
    public void MixedCaseExtension_IsRefusedWithAMessageNamingTheRealFault()
    {
        // Block 3 remediation, lower-severity finding: "Page.MD" is enumerated (Walk matches ".md"
        // case-insensitively) but can never round-trip (TryDecodeCore always reconstructs a lowercase
        // extension), so it is refused exactly like a D12 route collision even though no second file is
        // involved and no space/underscore ambiguity exists at all -- the fault is the extension's case,
        // not a routing collision. Asserts the corrected message names the real fault rather than just
        // pinning the (unchanged) refusal outcome.
        WriteFile("Page.MD");
        WriteFile("unaffected.md");

        var logs = new CapturingLogger<PageEnumerationService>();
        var result = new PageEnumerationService(Paths, logs).EnumeratePages();

        Assert.DoesNotContain(result.Pages, p => p.Route == "Page");
        var refused = Assert.Single(result.AmbiguousRoutes);
        Assert.Equal("Page", refused.Route);
        Assert.Equal(["Page.MD"], refused.RelativePaths);

        var unaffected = Assert.Single(result.Pages);
        Assert.Equal("unaffected", unaffected.Route);

        var message = Assert.Single(logs.Messages);
        Assert.Contains("extension", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Page.MD", message, StringComparison.Ordinal);
        Assert.DoesNotContain("D12", message, StringComparison.Ordinal);
    }

    /// <summary>Records formatted log messages so a test can assert on their text, not just that logging happened.</summary>
    private sealed class CapturingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    [Fact]
    public void SymlinkedDirectory_IsNotFollowed()
    {
        WriteFile(Path.Combine("real", "page.md"));
        var linkPath = Path.Combine(Paths.WorkingTree, "linked");

        try
        {
            Directory.CreateSymbolicLink(linkPath, Path.Combine(Paths.WorkingTree, "real"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Symlink creation can be unavailable/unprivileged on some CI sandboxes; the property under
            // test (do not follow) cannot be exercised there, so skip rather than fail on an environment
            // limitation unrelated to PageEnumerationService's own behaviour.
            return;
        }

        var result = CreateService().EnumeratePages();

        Assert.Single(result.Pages, p => p.Route == "real/page");
        Assert.DoesNotContain(result.Pages, p => p.Route.StartsWith("linked/", StringComparison.Ordinal));
    }

    [Fact]
    public void SymlinkedFile_IsNotFollowed()
    {
        WriteFile("target.md");
        var linkPath = Path.Combine(Paths.WorkingTree, "linked.md");

        try
        {
            File.CreateSymbolicLink(linkPath, Path.Combine(Paths.WorkingTree, "target.md"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var result = CreateService().EnumeratePages();

        Assert.DoesNotContain(result.Pages, p => p.Route == "linked");
        Assert.Contains(result.Pages, p => p.Route == "target");
    }

    [Fact]
    public void UnreadableDirectory_IsReportedAndOtherPagesStayServed()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        WriteFile(Path.Combine("locked", "secret.md"));
        WriteFile("visible.md");
        var lockedDir = Path.Combine(Paths.WorkingTree, "locked");

        File.SetUnixFileMode(lockedDir, UnixFileMode.None);
        try
        {
            bool stillReadable;
            try
            {
                Directory.EnumerateFileSystemEntries(lockedDir).ToList();
                stillReadable = true;
            }
            catch (UnauthorizedAccessException)
            {
                stillReadable = false;
            }

            if (stillReadable)
            {
                // Running as a user that ignores permission bits (e.g. root in CI) — this test's premise
                // cannot be exercised in this environment; nothing to assert.
                return;
            }

            var result = CreateService().EnumeratePages();

            Assert.Contains("locked", result.UnreadableDirectories);
            Assert.DoesNotContain(result.Pages, p => p.Route.StartsWith("locked", StringComparison.Ordinal));
            Assert.Contains(result.Pages, p => p.Route == "visible");
        }
        finally
        {
            File.SetUnixFileMode(lockedDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
