using Microsoft.Extensions.Logging;

namespace ZeroWiki.Content;

/// <summary>
/// D19: reacts to a received push whose <c>HEAD</c> moved from <paramref name="beforeSha"/> to
/// <paramref name="afterSha"/> — warms the page index (8.1) and broadcasts a "changed on disk" signal to
/// viewers of exactly the routes the push touched (8.2). The caller captures both shas <b>inside</b>
/// <c>RepositoryWriteLock</c>, bracketing its own <c>git-receive-pack</c> invocation (D19 §1), and passes
/// them here as fixed values; this type never re-reads "current <c>HEAD</c>" itself, so a later writer
/// landing before this reaction runs can never be attributed to this push's diff.
/// </summary>
/// <remarks>
/// <b>Runs off the request (D19 decision 1, block B).</b> Measured against a real push over a really-
/// listening Kestrel (block B DEVLOG thread): the git client does not observe its push as complete until
/// the server's request delegate itself returns — an artificial 2-second delay placed after the CGI
/// invocation added exactly that much wall-clock time to the client-observed push, even though the
/// response body had already been fully written and the client never disconnected early. Running this
/// reaction in-request would therefore tax every push by the reaction's own latency, structurally, not
/// merely in a rare disconnect case. Running it here — resolved from already-<em>singleton</em> services
/// (never request-scoped, so nothing here depends on <c>HttpContext.RequestServices</c> outliving the
/// request) and driven by <see cref="Microsoft.Extensions.Hosting.IHostApplicationLifetime.ApplicationStopping"/>
/// rather than <c>HttpContext.RequestAborted</c> — decouples the push's own response from this work
/// entirely, structurally rather than by the reaction happening to finish before anything would notice.
/// </remarks>
/// <remarks>
/// <b>A failed reaction must never fail the push (D19 decision 2).</b> The push has already landed and
/// been acknowledged to the client by the time this type is ever invoked; every exception this method
/// can produce beyond a genuine <see cref="OperationCanceledException"/> (app shutdown) is caught and
/// logged at <see cref="LogLevel.Warning"/> — not <c>Error</c>, because nothing about the push itself
/// failed, only the index warm or the broadcast that follows it — and never rethrown.
/// </remarks>
public sealed class PushReactionService
{
    private readonly ContentPaths _paths;
    private readonly GitProcessRunner _git;
    private readonly PageHistoryService _historyService;
    private readonly PageIndex _pageIndex;
    private readonly IPageChangeNotifier _notifier;
    private readonly ILogger<PushReactionService> _logger;

    public PushReactionService(
        ContentPaths paths,
        GitProcessRunner git,
        PageHistoryService historyService,
        PageIndex pageIndex,
        IPageChangeNotifier notifier,
        ILogger<PushReactionService> logger)
    {
        _paths = paths;
        _git = git;
        _historyService = historyService;
        _pageIndex = pageIndex;
        _notifier = notifier;
        _logger = logger;
    }

    /// <summary>
    /// The whole reaction, wrapped so nothing it does can escape as an unhandled exception (see this
    /// type's own remarks). <paramref name="beforeSha"/>/<paramref name="afterSha"/> may each be
    /// <see langword="null"/> for an unborn <c>HEAD</c> (D15's "no pages") — in practice unreachable once
    /// the app has started, since startup always creates an initial commit before any push route is
    /// reachable, but handled rather than assumed away.
    /// </summary>
    public async Task ReactAsync(string? beforeSha, string? afterSha, CancellationToken cancellationToken)
    {
        if (string.Equals(beforeSha, afterSha, StringComparison.Ordinal))
        {
            // D19 §2: `before == after` is what means "nothing to react to" -- never the subprocess
            // exit code, and never an assumption about whether a rejected or no-op push even reached
            // this far. The caller already applies this check before ever calling this method; it is
            // re-asserted here so this type is correct on its own terms, not only when called correctly.
            return;
        }

        try
        {
            // 8.1: warm the index. D15's own lazy stamp check already covers correctness for every
            // writer, including one that never notifies the app at all (the scenario this reuses), so
            // this eager call buys latency for the first reader after the push, not correctness (D19
            // §4) -- it is independent of whether the route diff below succeeds.
            await _pageIndex.GetCurrentAsync(cancellationToken).ConfigureAwait(false);

            if (beforeSha is null || afterSha is null)
            {
                // No previous (or no current) commit to diff against -- there is no prior servable
                // state anyone could have been viewing, so there is nothing meaningful to name as
                // "changed" for 8.2. The index warm above still ran.
                _logger.LogDebug(
                    "Push reaction skipped the route diff for an unborn HEAD transition " +
                    "({BeforeSha} -> {AfterSha}); the page index was still warmed.",
                    beforeSha ?? "(none)",
                    afterSha ?? "(none)");
                return;
            }

            var changedRoutes = await ComputeChangedRoutesAsync(beforeSha, afterSha, cancellationToken)
                .ConfigureAwait(false);

            await _notifier.NotifyChangedAsync(changedRoutes, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ordinarily app shutdown (ApplicationStopping) -- not a fault of the push, which already
            // landed and was already acknowledged; nothing to log as a warning.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "The push reaction for HEAD {BeforeSha} -> {AfterSha} failed; the push itself already " +
                "succeeded and is unaffected. The next page view of any affected page will still refresh " +
                "correctly (D15's own lazy stamp check), just without this push's eager warm or broadcast.",
                beforeSha,
                afterSha);
        }
    }

    /// <summary>
    /// D19 §2's diff, in the same shape <see cref="PageIndexBuilder.RefreshAsync"/> already uses for its
    /// own incremental refresh -- mapped to the encoded routes the diff's paths would resolve to under
    /// D12, without ever consulting D12's uniqueness/ambiguity rule (D19 decision 3): whether a changed
    /// path's route is currently servable is a question this method has no need to answer. A route that
    /// collides with another (D12's own non-injective <see cref="PageRouteCodec.Encode"/>) is still a
    /// route nobody could ever have loaded and subscribed to -- <c>WikiPage.razor</c> refuses to render
    /// an ambiguous route's content, and only the rendered-body branch mounts the subscribing indicator
    /// component -- so notifying it anyway is a harmless no-op, not a hazard needing detection here.
    /// </summary>
    private async Task<IReadOnlyList<EncodedRoute>> ComputeChangedRoutesAsync(
        string beforeSha,
        string afterSha,
        CancellationToken cancellationToken)
    {
        var diffArguments = new[]
        {
            "-c", "core.quotePath=false",
            "diff", "--no-renames", "--name-only", "-z",
            beforeSha, afterSha,
            "--", _historyService.RepositoryRelativeWorkingTree,
        };

        var diffResult = await _git
            .RunOrThrowAsync(_paths.RepositoryRoot, diffArguments, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var workingTreePrefix = _historyService.RepositoryRelativeWorkingTree + "/";
        var routes = new List<EncodedRoute>();

        foreach (var token in diffResult.StandardOutput.Split('\x00'))
        {
            if (token.Length == 0 || !token.StartsWith(workingTreePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var relativePath = token[workingTreePrefix.Length..];

            // Same candidacy rule PageEnumerationService/PageIndexBuilder apply elsewhere -- an ordinary,
            // lowercase-or-not ".md" file, no dot-prefixed segment (an Obsidian vault's .obsidian/).
            // Anything else was never a page candidate, so it cannot name a route worth notifying.
            if (!relativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
                HasDotPrefixedSegment(relativePath))
            {
                continue;
            }

            try
            {
                routes.Add(PageRouteCodec.Encode(relativePath));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Defensive rather than reachable: Encode is total for any non-null/empty path, and the
                // filters above already exclude the one input it refuses (empty). Never lets one bad
                // path drop the rest of this push's routes (D19 decision 3).
                _logger.LogWarning(
                    ex,
                    "Could not map changed path '{RelativePath}' to a page route for the push reaction; " +
                    "its viewers, if any, will not be notified for this push.",
                    relativePath);
            }
        }

        return routes.Distinct().ToList();
    }

    private static bool HasDotPrefixedSegment(string relativePath) =>
        relativePath.Split('/').Any(segment => segment.StartsWith('.'));
}
