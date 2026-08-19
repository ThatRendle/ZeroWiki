using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ZeroWiki.Content;
using ZeroWiki.Tests.Identity;

namespace ZeroWiki.Tests.Content;

/// <summary>
/// D19, §8 block B: <see cref="PushReactionService.ReactAsync"/> against a real git repository —
/// exactly the diff shape (<c>git diff --name-only</c> between two fixed shas) a caller inside
/// <c>GitSmartHttpEndpoints.HandleReceivePackAsync</c> would hand it, never something this type
/// re-derives from "current HEAD" itself (D19 §1). <see cref="PageChangeNotifierTests"/> covers the
/// notifier's own fan-out; this file covers only "were the right routes computed and handed to it."
/// </summary>
public sealed class PushReactionServiceTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"zerowiki-push-reaction-{Guid.NewGuid():n}");
    private readonly GitProcessRunner _git = new();
    private readonly RecordingPageChangeNotifier _notifier = new();

    private ContentPaths Paths => new(_dataRoot);

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
    }

    /// <summary>
    /// <paramref name="notifier"/>/<paramref name="logger"/> default to the recording fake and a
    /// null logger -- overridden by §12.1's log-record tests, which need the real
    /// <see cref="PageChangeNotifier"/> (so a genuine <see cref="PageChangeNotificationResult"/> is
    /// what reaches the log call) and a capturing logger (so the record can be asserted structurally).
    /// </summary>
    private PushReactionService CreateService(
        IPageChangeNotifier? notifier = null, ILogger<PushReactionService>? logger = null)
    {
        var paths = Paths;
        var historyService = new PageHistoryService(paths, _git, NullLogger<PageHistoryService>.Instance);
        var frontmatterExtractor = new PageFrontmatterExtractor(MarkdownPipelineFactory.Create(), new SharpYamlFrontmatterParser());
        var builder = new PageIndexBuilder(
            paths,
            _git,
            new PageEnumerationService(paths, NullLogger<PageEnumerationService>.Instance),
            frontmatterExtractor,
            historyService,
            NullLogger<PageIndexBuilder>.Instance);
        var pageIndex = new PageIndex(builder, NullLogger<PageIndex>.Instance);

        return new PushReactionService(
            paths,
            _git,
            historyService,
            pageIndex,
            notifier ?? _notifier,
            logger ?? NullLogger<PushReactionService>.Instance);
    }

    /// <summary>Builds a service whose <see cref="PageIndex"/> is backed by <paramref name="builder"/>
    /// instead of a real <see cref="PageIndexBuilder"/> -- the seam that lets a test force the 8.1 warm
    /// to throw without needing to corrupt the repository the 8.2 diff also reads from.</summary>
    private PushReactionService CreateServiceWithPageIndexBuilder(IPageIndexBuilder builder)
    {
        var paths = Paths;
        var historyService = new PageHistoryService(paths, _git, NullLogger<PageHistoryService>.Instance);
        var pageIndex = new PageIndex(builder, NullLogger<PageIndex>.Instance);

        return new PushReactionService(
            paths, _git, historyService, pageIndex, _notifier, NullLogger<PushReactionService>.Instance);
    }

    private async Task InitializeRepositoryAsync()
    {
        var repositoryRoot = Paths.RepositoryRoot;
        Directory.CreateDirectory(Path.Combine(repositoryRoot, "docs"));

        await _git.RunOrThrowAsync(repositoryRoot, ["init", "-b", "main"]);
        await _git.RunOrThrowAsync(repositoryRoot, ["config", "user.name", "placeholder"]);
        await _git.RunOrThrowAsync(repositoryRoot, ["config", "user.email", "placeholder@example.invalid"]);
    }

    private async Task<string> CommitAsync(string relativePath, string content)
    {
        var repositoryRoot = Paths.RepositoryRoot;
        var full = Path.Combine(repositoryRoot, "docs", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content);

        var docsRelative = "docs/" + relativePath.Replace(Path.DirectorySeparatorChar, '/');
        await _git.RunOrThrowAsync(repositoryRoot, ["add", docsRelative]);
        await _git.RunOrThrowAsync(
            repositoryRoot,
            ["commit", "-m", "page"],
            new GitAuthor("Alice", "alice@zerowiki.example").ToEnvironmentVariables());

        var head = await _git.RunOrThrowAsync(repositoryRoot, ["rev-parse", "HEAD"]);
        return head.StandardOutput.Trim();
    }

    private async Task<string> CurrentHeadAsync() =>
        (await _git.RunOrThrowAsync(Paths.RepositoryRoot, ["rev-parse", "HEAD"])).StandardOutput.Trim();

    [Fact]
    public async Task ReactAsync_BeforeEqualsAfter_NeverNotifies()
    {
        // D19 §2: `before == after` is what means "nothing to react to" -- the same sha twice, exactly
        // what a server-side-rejected or no-op push leaves the caller holding regardless of exit code.
        await InitializeRepositoryAsync();
        var sha = await CommitAsync("page.md", "content");

        await CreateService().ReactAsync(sha, sha, CancellationToken.None);

        Assert.Empty(_notifier.Calls);
    }

    [Fact]
    public async Task ReactAsync_BothShasNull_NeverNotifies()
    {
        await CreateService().ReactAsync(null, null, CancellationToken.None);

        Assert.Empty(_notifier.Calls);
    }

    [Fact]
    public async Task ReactAsync_ChangedPage_NotifiesExactlyItsRoute()
    {
        await InitializeRepositoryAsync();
        var before = await CommitAsync("unrelated.md", "unrelated content");
        var after = await CommitAsync("page.md", "new content");

        await CreateService().ReactAsync(before, after, CancellationToken.None);

        var call = Assert.Single(_notifier.Calls);
        var route = Assert.Single(call);
        Assert.Equal("page", route.Value);
    }

    [Fact]
    public async Task ReactAsync_MultiplePagesChanged_NotifiesAllOfThemAndNothingElse()
    {
        await InitializeRepositoryAsync();
        var before = await CommitAsync("untouched.md", "stays the same");

        var repositoryRoot = Paths.RepositoryRoot;
        await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "docs", "first.md"), "one");
        await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "docs", "second.md"), "two");
        await _git.RunOrThrowAsync(repositoryRoot, ["add", "-A"]);
        await _git.RunOrThrowAsync(
            repositoryRoot, ["commit", "-m", "two pages"], new GitAuthor("Alice", "alice@zerowiki.example").ToEnvironmentVariables());
        var after = await CurrentHeadAsync();

        await CreateService().ReactAsync(before, after, CancellationToken.None);

        var call = Assert.Single(_notifier.Calls);
        Assert.Equal(2, call.Count);
        Assert.Contains(call, r => r.Value == "first");
        Assert.Contains(call, r => r.Value == "second");
        Assert.DoesNotContain(call, r => r.Value == "untouched");
    }

    [Fact]
    public async Task ReactAsync_ChangeUnderADotPrefixedDirectory_IsExcludedFromTheNotifiedRoutes()
    {
        // An Obsidian vault's .obsidian/ -- never a page candidate, so it can never name a route worth
        // notifying (mirrors PageEnumerationService/PageIndexBuilder's own rule).
        await InitializeRepositoryAsync();
        var before = await CommitAsync("page.md", "content");

        var repositoryRoot = Paths.RepositoryRoot;
        var dotDir = Path.Combine(repositoryRoot, "docs", ".obsidian");
        Directory.CreateDirectory(dotDir);
        await File.WriteAllTextAsync(Path.Combine(dotDir, "config.json"), "{}");
        await _git.RunOrThrowAsync(repositoryRoot, ["add", "-A"]);
        await _git.RunOrThrowAsync(
            repositoryRoot, ["commit", "-m", "obsidian config"], new GitAuthor("Alice", "alice@zerowiki.example").ToEnvironmentVariables());
        var after = await CurrentHeadAsync();

        await CreateService().ReactAsync(before, after, CancellationToken.None);

        var call = Assert.Single(_notifier.Calls);
        Assert.Empty(call);
    }

    [Fact]
    public async Task ReactAsync_UnresolvableShas_LogsAndDoesNotThrow()
    {
        await InitializeRepositoryAsync();
        await CommitAsync("page.md", "content");

        var bogusBefore = new string('a', 40);
        var bogusAfter = new string('b', 40);

        // Must not throw -- the push already landed and was already acknowledged; a diff this type
        // cannot compute is a warning, never a fault that escapes this method (D19 decision 2).
        await CreateService().ReactAsync(bogusBefore, bogusAfter, CancellationToken.None);

        Assert.Empty(_notifier.Calls);
    }

    [Fact]
    public async Task ReactAsync_NullBeforeSha_WarmsTheIndexButNeverNotifies()
    {
        // An unborn-to-born HEAD transition (in practice unreachable once the app has started, since
        // startup always creates an initial commit first) -- there is no prior servable state anyone
        // could have been viewing, so there is nothing to diff or to notify.
        await InitializeRepositoryAsync();
        var after = await CommitAsync("page.md", "content");

        await CreateService().ReactAsync(null, after, CancellationToken.None);

        Assert.Empty(_notifier.Calls);
    }

    [Fact]
    public async Task ReactAsync_IndexWarmThrows_StillComputesAndBroadcastsTheChangedRoutes()
    {
        // §8 remediation (supervisor finding): 8.1 (the index warm) and 8.2 (the spec-required diff
        // and broadcast) must fail independently. Forces the warm to throw via a fake IPageIndexBuilder
        // -- PageIndex.GetCurrentAsync calls ProbeCurrentHeadShaAsync directly and has nothing of its
        // own to catch a builder fault with -- and asserts the broadcast still happens with the correct
        // routes regardless. Fails if the two are ever re-coupled behind one try/catch.
        await InitializeRepositoryAsync();
        var before = await CommitAsync("unrelated.md", "unrelated content");
        var after = await CommitAsync("page.md", "new content");

        await CreateServiceWithPageIndexBuilder(new ThrowingPageIndexBuilder())
            .ReactAsync(before, after, CancellationToken.None);

        var call = Assert.Single(_notifier.Calls);
        var route = Assert.Single(call);
        Assert.Equal("page", route.Value);
    }

    /// <summary>
    /// §12.1's own falsifier, at this layer: the structured record for a delivered reaction and for a
    /// zero-match one must differ, and the assertions below are on the returned key/value state
    /// (<see cref="CapturingLoggerProvider.LogEntry.Values"/>), never on the rendered message text.
    /// </summary>
    [Fact]
    public async Task ReactAsync_ChangedPage_EmitsOneStructuredInformationRecordWithAllThreeQuantities()
    {
        await InitializeRepositoryAsync();
        var before = await CommitAsync("unrelated.md", "unrelated content");
        var after = await CommitAsync("page.md", "new content");

        var realNotifier = new PageChangeNotifier(NullLogger<PageChangeNotifier>.Instance);
        using var subscription = realNotifier.Subscribe(PageRouteCodec.Encode("page.md"), () => Task.CompletedTask);

        var loggerProvider = new CapturingLoggerProvider();
        await CreateService(realNotifier, loggerProvider.CreateLogger<PushReactionService>())
            .ReactAsync(before, after, CancellationToken.None);

        var entry = Assert.Single(loggerProvider.Entries, e => e.Level == LogLevel.Information);
        var values = entry.Values.ToDictionary(kv => kv.Key, kv => kv.Value);

        Assert.Equal(1, values["DiffedRouteCount"]);
        Assert.Equal(1, values["SubscribersMatched"]);
        Assert.Equal(1, values["CallbacksInvoked"]);
        // A healthy delivery never carries the zero-match diagnostic (PageChangeNotifier never
        // computes it for one) -- there is nothing to assert its absence against other than the key
        // itself never having been logged.
        Assert.DoesNotContain("SubscribedRoutes", values.Keys);
    }

    /// <summary>
    /// The architect's addition to §12.1: a non-empty diff that matches nobody must carry the routes
    /// the subscription table actually held, distinguishing "nothing is subscribed at all" from
    /// "something is subscribed, just not to this route" -- two different defects a bare zero cannot
    /// tell apart.
    /// </summary>
    [Fact]
    public async Task ReactAsync_ChangedPageNobodySubscribesTo_RecordCarriesTheSubscribedRoutesInstead()
    {
        await InitializeRepositoryAsync();
        var before = await CommitAsync("unrelated.md", "unrelated content");
        var after = await CommitAsync("page.md", "new content");

        var realNotifier = new PageChangeNotifier(NullLogger<PageChangeNotifier>.Instance);
        var elsewhereRoute = PageRouteCodec.Encode("elsewhere.md");
        using var subscription = realNotifier.Subscribe(elsewhereRoute, () => Task.CompletedTask);

        var loggerProvider = new CapturingLoggerProvider();
        await CreateService(realNotifier, loggerProvider.CreateLogger<PushReactionService>())
            .ReactAsync(before, after, CancellationToken.None);

        var entry = Assert.Single(loggerProvider.Entries, e => e.Level == LogLevel.Information);
        var values = entry.Values.ToDictionary(kv => kv.Key, kv => kv.Value);

        Assert.Equal(0, values["SubscribersMatched"]);
        Assert.Equal(0, values["CallbacksInvoked"]);
        Assert.Equal(1, values["SubscribedRouteCount"]);
        Assert.Equal($"[\"{elsewhereRoute.Value}\"]", values["SubscribedRoutes"]);
    }

    /// <summary>
    /// §12 correction's own falsifier: an empty subscription table and a table holding exactly one
    /// <c>default(EncodedRoute)</c> (obtainable from any caller regardless of that type's
    /// <see langword="internal"/> constructor -- see its own remarks) must render as different text, not
    /// merely as different structured values a reader would have to already know to query for. Both
    /// states are driven through the same real <see cref="PushReactionService"/> -&gt;
    /// <see cref="PageChangeNotifier"/> path and asserted on <see cref="CapturingLoggerProvider.LogEntry.Message"/>
    /// -- the actual text a human or a log sink reads -- because that is exactly the field the withdrawn
    /// live-run conclusion was drawn from and where the previous round's tests never looked.
    /// </summary>
    [Fact]
    public async Task ReactAsync_ZeroMatchWithNoSubscriptions_And_ZeroMatchWithADefaultRouteSubscription_RenderDifferently()
    {
        await InitializeRepositoryAsync();
        var before = await CommitAsync("unrelated.md", "unrelated content");
        var after = await CommitAsync("page.md", "new content");

        async Task<string> RenderedMessageAsync(PageChangeNotifier notifier)
        {
            var loggerProvider = new CapturingLoggerProvider();
            await CreateService(notifier, loggerProvider.CreateLogger<PushReactionService>())
                .ReactAsync(before, after, CancellationToken.None);
            return Assert.Single(loggerProvider.Entries, e => e.Level == LogLevel.Information).Message;
        }

        var noSubscriptions = await RenderedMessageAsync(new PageChangeNotifier(NullLogger<PageChangeNotifier>.Instance));

        var defaultRouteNotifier = new PageChangeNotifier(NullLogger<PageChangeNotifier>.Instance);
        using var subscription = defaultRouteNotifier.Subscribe(default, () => Task.CompletedTask);
        var oneDefaultRouteSubscription = await RenderedMessageAsync(defaultRouteNotifier);

        Assert.NotEqual(noSubscriptions, oneDefaultRouteSubscription);
        Assert.Contains("held 0 route(s) []", noSubscriptions);
        Assert.Contains("held 1 route(s) [<null>]", oneDefaultRouteSubscription);
    }

    /// <summary>
    /// §12 second correction's own falsifier (reviewer finding: <see cref="PageRouteCodec.EncodeSegment"/>
    /// leaves <c>"</c> unescaped, so a real route <see cref="EncodedRoute.Value"/> can contain one).
    /// Reachable without a raw space -- see the §12 second correction post's own reachability finding:
    /// <c>PageRouteCodec.Encode("a\",\"b.md").Value == "a\",\"b"</c>, a raw unescaped quote and comma,
    /// with no space needed. Pre-escaping, a single subscription under that route rendered the
    /// <c>SubscribedRoutes</c> field as <c>["a","b"]</c> -- byte-identical to the compact, no-space
    /// bracket-and-quote notation (JSON's included) for a genuinely different <em>two</em>-element list
    /// <c>["a","b"]</c>. This is not a claim that it collides with <em>this codebase's own</em>
    /// <see cref="PushReactionService"/> multi-item rendering, which always separates items with a real
    /// space (<c>Encode</c> can never produce one -- see the same post) and therefore never renders this
    /// exact text for two items; it is a claim about the general notation the syntax borrows from, which
    /// is what a human skimming the log or a naive log-scraping tool reads it as.
    /// </summary>
    [Fact]
    public async Task ReactAsync_SubscribedRouteValueContainingAnUnescapedQuote_NoLongerMimicsATwoElementList()
    {
        await InitializeRepositoryAsync();
        var before = await CommitAsync("unrelated.md", "unrelated content");
        var after = await CommitAsync("page.md", "new content");

        async Task<string> RenderedMessageAsync(PageChangeNotifier notifier)
        {
            var loggerProvider = new CapturingLoggerProvider();
            await CreateService(notifier, loggerProvider.CreateLogger<PushReactionService>())
                .ReactAsync(before, after, CancellationToken.None);
            return Assert.Single(loggerProvider.Entries, e => e.Level == LogLevel.Information).Message;
        }

        var mimicryRoute = PageRouteCodec.Encode("a\",\"b.md");
        var notifier = new PageChangeNotifier(NullLogger<PageChangeNotifier>.Instance);
        using var subscription = notifier.Subscribe(mimicryRoute, () => Task.CompletedTask);

        var rendered = await RenderedMessageAsync(notifier);

        static string Quote(string s) => "\"" + s + "\"";
        static string Escape(string raw) => raw
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

        var compactTwoElementNotation = "[" + Quote("a") + "," + Quote("b") + "]";
        var theOneEscapedItemThatIsActuallyThere = "[" + Quote(Escape(mimicryRoute.Value)) + "]";

        Assert.DoesNotContain(compactTwoElementNotation, rendered);
        Assert.Contains(theOneEscapedItemThatIsActuallyThere, rendered);
    }

    /// <summary>
    /// §12 second correction: pins the reviewer's other reachable case -- a real route whose text
    /// happens to be the seven characters <c>&lt;null&gt;</c> (<c>PageRouteCodec.EncodeSegment</c>
    /// leaves <c>&lt;</c>/<c>&gt;</c> unescaped too) must still render distinguishably from
    /// <c>default(EncodedRoute)</c>'s sentinel -- the quotes around a real value are what carry that
    /// distinction (<c>["&lt;null&gt;"]</c> vs <c>[&lt;null&gt;]</c>), not anything this correction adds,
    /// but the reviewer established it is reachable, so it is pinned rather than left to accident.
    /// </summary>
    [Fact]
    public async Task ReactAsync_SubscribedRouteWhoseTextIsLiterallyNullAngleBrackets_RendersDifferentlyFromTheDefaultSentinel()
    {
        await InitializeRepositoryAsync();
        var before = await CommitAsync("unrelated.md", "unrelated content");
        var after = await CommitAsync("page.md", "new content");

        async Task<string> RenderedMessageAsync(PageChangeNotifier notifier)
        {
            var loggerProvider = new CapturingLoggerProvider();
            await CreateService(notifier, loggerProvider.CreateLogger<PushReactionService>())
                .ReactAsync(before, after, CancellationToken.None);
            return Assert.Single(loggerProvider.Entries, e => e.Level == LogLevel.Information).Message;
        }

        var literalTextRoute = PageRouteCodec.Encode("<null>.md");
        Assert.Equal("<null>", literalTextRoute.Value);

        var literalTextNotifier = new PageChangeNotifier(NullLogger<PageChangeNotifier>.Instance);
        using var literalSubscription = literalTextNotifier.Subscribe(literalTextRoute, () => Task.CompletedTask);
        var literalTextRendering = await RenderedMessageAsync(literalTextNotifier);

        var defaultRouteNotifier = new PageChangeNotifier(NullLogger<PageChangeNotifier>.Instance);
        using var defaultSubscription = defaultRouteNotifier.Subscribe(default, () => Task.CompletedTask);
        var defaultSentinelRendering = await RenderedMessageAsync(defaultRouteNotifier);

        Assert.NotEqual(literalTextRendering, defaultSentinelRendering);
        Assert.Contains("held 1 route(s) [\"<null>\"]", literalTextRendering);
        Assert.Contains("held 1 route(s) [<null>]", defaultSentinelRendering);
    }

    private sealed class ThrowingPageIndexBuilder : IPageIndexBuilder
    {
        public Task<string?> ProbeCurrentHeadShaAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Simulated index-warm failure.");

        public Task<PageIndexSnapshot> RefreshAsync(
            PageIndexSnapshot current, string? currentHeadSha, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Simulated index-warm failure.");
    }

    private sealed class RecordingPageChangeNotifier : IPageChangeNotifier
    {
        public List<IReadOnlyCollection<EncodedRoute>> Calls { get; } = [];

        public IDisposable Subscribe(EncodedRoute route, Func<Task> onChanged) =>
            throw new NotSupportedException("PushReactionService never subscribes; only viewers do.");

        public Task<PageChangeNotificationResult> NotifyChangedAsync(
            IReadOnlyCollection<EncodedRoute> routes, CancellationToken cancellationToken)
        {
            Calls.Add(routes);
            return Task.FromResult(new PageChangeNotificationResult(0, 0, null));
        }
    }
}
