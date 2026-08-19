using System.Linq;
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
/// <remarks>
/// <b>The index warm (8.1) and the route diff/broadcast (8.2) fail independently, in both directions
/// (§8 remediation, supervisor finding).</b> An earlier revision ran both inside one <c>try</c>, warm
/// first: true to D19 §4 in the direction it stated ("[the warm] is independent of whether the route
/// diff below succeeds") but silent about the direction that matters — a warm that <em>throws</em>
/// would abort the method before the diff/broadcast ever ran, silently dropping 8.2's own
/// spec-required work (<c>specs/git-sync/spec.md</c>'s <em>Re-index and broadcast on received push</em>)
/// behind a failure in what is, by D19 §4's own account, a latency-only optimisation. <see cref="WarmPageIndexAsync"/>
/// and the diff/broadcast below now each own their exception handling, so a failed warm can never
/// prevent the broadcast from running, and — symmetrically — a failed diff/broadcast can never prevent
/// the warm from having already run. Pinned by
/// <c>PushReactionServiceTests.ReactAsync_IndexWarmThrows_StillComputesAndBroadcastsTheChangedRoutes</c>.
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

        // 8.1 and 8.2 are decoupled, deliberately not one shared try/catch (see this type's own
        // remarks): a failed warm must never drop the spec-required broadcast, and a failed
        // diff/broadcast must never appear to have skipped a warm that actually ran.
        await WarmPageIndexAsync(cancellationToken).ConfigureAwait(false);

        if (beforeSha is null || afterSha is null)
        {
            // No previous (or no current) commit to diff against -- there is no prior servable state
            // anyone could have been viewing, so there is nothing meaningful to name as "changed" for
            // 8.2. The index warm above still ran (or was attempted) regardless.
            _logger.LogDebug(
                "Push reaction skipped the route diff for an unborn HEAD transition " +
                "({BeforeSha} -> {AfterSha}).",
                beforeSha ?? "(none)",
                afterSha ?? "(none)");
            return;
        }

        await ComputeAndBroadcastChangedRoutesAsync(beforeSha, afterSha, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 8.1: warms the index. D15's own lazy stamp check already covers correctness for every writer,
    /// including one that never notifies the app at all (the scenario this reuses), so this eager call
    /// buys latency for the first reader after the push, not correctness (D19 §4). Never lets an
    /// exception escape -- see this type's own remarks on why this is its own try/catch rather than
    /// sharing one with <see cref="ComputeAndBroadcastChangedRoutesAsync"/>.
    /// </summary>
    private async Task WarmPageIndexAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _pageIndex.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
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
                "The push reaction's index warm failed; the push itself already succeeded and is " +
                "unaffected, and the route diff/broadcast below still runs regardless. The next page " +
                "view of any affected page will still refresh correctly (D15's own lazy stamp check), " +
                "just without this push's eager warm.");
        }
    }

    /// <summary>
    /// 8.2: the route diff and broadcast (D19 §2/§3) — the spec-required half of this reaction. Never
    /// lets an exception escape; see this type's own remarks.
    /// </summary>
    private async Task ComputeAndBroadcastChangedRoutesAsync(
        string beforeSha,
        string afterSha,
        CancellationToken cancellationToken)
    {
        try
        {
            var changedRoutes = await ComputeChangedRoutesAsync(beforeSha, afterSha, cancellationToken)
                .ConfigureAwait(false);

            var result = await _notifier.NotifyChangedAsync(changedRoutes, cancellationToken).ConfigureAwait(false);

            LogReactionOutcome(beforeSha, afterSha, changedRoutes, result);
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
                "The push reaction's route diff/broadcast for HEAD {BeforeSha} -> {AfterSha} failed; " +
                "the push itself already succeeded and is unaffected. The next page view of any " +
                "affected page will still refresh correctly (D15's own lazy stamp check), just without " +
                "this push's broadcast.",
                beforeSha,
                afterSha);
        }
    }

    /// <summary>
    /// §12.1 (<c>specs/git-sync/spec.md</c>'s <em>The reaction to a push is observable</em>): one
    /// structured <see cref="LogLevel.Information"/> record per reaction carrying the three quantities
    /// the spec names -- the routes diffed, the subscribers matched, and the callbacks invoked -- read
    /// together rather than scattered across separate lines a reader would have to correlate under a
    /// concurrent push. This is what makes a broadcast reaching no viewer distinguishable from a
    /// delivered one; the outcome is unrecorded on <see cref="ComputeAndBroadcastChangedRoutesAsync"/>'s
    /// own failure path above, which already logs its own <see cref="LogLevel.Warning"/> and is a
    /// different concern (§8's supervisor already made the warm and the broadcast fail independently).
    /// </summary>
    /// <remarks>
    /// When <paramref name="result"/> matched no subscriber against a non-empty diff, the record also
    /// carries the routes the subscription table actually held at that moment (route values only, never
    /// subscriber identities) -- the diagnostic that turns "matched 0" into "diffed X, subscribed []"
    /// (no circuit ever subscribed) versus "diffed X, subscribed Y" (subscribed, just not under X). A
    /// healthy delivery never carries this; <see cref="PageChangeNotifier"/> never computes it for one.
    /// </remarks>
    /// <remarks>
    /// <b>§12 correction.</b> Both route lists are rendered through <see cref="FormatRoutes"/> rather
    /// than interpolated directly: <c>Microsoft.Extensions.Logging</c>'s default placeholder formatter
    /// enumerates an <see cref="IEnumerable{T}"/> argument and joins the items with no wrapping
    /// delimiter, so a 0-element list and a 1-element list holding <c>default(EncodedRoute)</c> (whose
    /// <see cref="EncodedRoute.Value"/> is <see langword="null"/> -- see that type's own remarks) both
    /// render as the empty string; measured directly against this logger, not assumed. The explicit
    /// <c>{SubscribedRouteCount}</c> placeholder is a plain <see langword="int"/>, so it renders
    /// correctly regardless of what the list contains and a reader is never left to infer a count from
    /// parsing the rendered list text.
    /// </remarks>
    private void LogReactionOutcome(
        string beforeSha,
        string afterSha,
        IReadOnlyList<EncodedRoute> changedRoutes,
        PageChangeNotificationResult result)
    {
        if (result.SubscribedRoutesAtZeroMatch is { } subscribedRoutes)
        {
            _logger.LogInformation(
                "Push reaction for HEAD {BeforeSha} -> {AfterSha} diffed {DiffedRouteCount} route(s) " +
                "{DiffedRoutes}, matched {SubscribersMatched} subscriber(s), and invoked " +
                "{CallbacksInvoked} callback(s); the subscription table held {SubscribedRouteCount} " +
                "route(s) {SubscribedRoutes} at that moment.",
                beforeSha,
                afterSha,
                changedRoutes.Count,
                FormatRoutes(changedRoutes),
                result.SubscribersMatched,
                result.CallbacksInvoked,
                subscribedRoutes.Count,
                FormatRoutes(subscribedRoutes));
            return;
        }

        _logger.LogInformation(
            "Push reaction for HEAD {BeforeSha} -> {AfterSha} diffed {DiffedRouteCount} route(s) " +
            "{DiffedRoutes}, matched {SubscribersMatched} subscriber(s), and invoked " +
            "{CallbacksInvoked} callback(s).",
            beforeSha,
            afterSha,
            changedRoutes.Count,
            FormatRoutes(changedRoutes),
            result.SubscribersMatched,
            result.CallbacksInvoked);
    }

    /// <summary>
    /// §12 correction: a bracket-delimited, comma-joined, quoted rendering of a route list -- so the
    /// text a reader (or log-scraping tool) sees cannot collapse two different states to the same bytes
    /// the way <c>Microsoft.Extensions.Logging</c>'s bare placeholder interpolation did (see
    /// <see cref="LogReactionOutcome"/>'s own remarks). An empty table renders <c>[]</c>; a table
    /// holding one route with an empty <see cref="EncodedRoute.Value"/> renders <c>[""]</c> -- visibly
    /// distinct from <c>[]</c> because the quotes are always present; a table holding one
    /// <c>default(EncodedRoute)</c> renders <c>[&lt;null&gt;]</c>, an unquoted sentinel because
    /// <see langword="null"/> cannot itself sit inside quotes without becoming indistinguishable from
    /// the empty-string case, and is itself distinguishable from a real route whose text happens to be
    /// the seven characters <c>&lt;null&gt;</c> -- that renders <c>["&lt;null&gt;"]</c>, quoted, never
    /// bare.
    /// </summary>
    /// <remarks>
    /// <b>§12 second correction -- <see cref="FormatRoute"/> escapes its content (reviewer finding).</b>
    /// Quoting alone only disambiguates a value from the list structure around it if the quote character
    /// itself cannot appear unescaped inside the value -- and <see cref="PageRouteCodec.EncodeSegment"/>
    /// leaves <c>"</c> (and <c>&lt;</c>, <c>&gt;</c>) unescaped, so a real, <c>Encode</c>-reachable route
    /// <see cref="EncodedRoute.Value"/> can contain one (verified: <c>Encode("a\",\"b.md").Value ==
    /// "a\",\"b"</c>). <see cref="FormatRoute"/> therefore backslash-escapes before quote-escaping --
    /// the same two-character scheme C and JSON string literals use, and total for the same reason
    /// theirs is: scanning a rendered value left to right, the first <c>"</c> preceded by an <i>even</i>
    /// number of consecutive <c>\</c> (zero included) is unambiguously the closing delimiter, because
    /// every <c>\</c> that originated in the raw value was itself doubled first, so a real <c>\"</c> pair
    /// from the source can never masquerade as escaped-backslash-then-bare-quote. That makes the mapping
    /// from raw value to escaped-and-quoted text injective over <i>every</i> string a <see langword="string"/>
    /// can hold, not merely the ones <see cref="PageRouteCodec.Encode"/> happens to produce today --
    /// <see cref="EncodedRoute"/>'s own contract promises nothing beyond "default has a null Value" (see
    /// its remarks), so this method does not lean on <c>Encode</c>'s specific transform (which, as it
    /// happens, never lets a raw space or backslash survive into <see cref="EncodedRoute.Value"/> at all
    /// -- checked, not assumed) as its only line of defence.
    /// </remarks>
    private static string FormatRoutes(IReadOnlyCollection<EncodedRoute> routes) =>
        "[" + string.Join(", ", routes.Select(FormatRoute)) + "]";

    private static string FormatRoute(EncodedRoute route) =>
        route.Value is null ? "<null>" : $"\"{EscapeForRendering(route.Value)}\"";

    private static string EscapeForRendering(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

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
