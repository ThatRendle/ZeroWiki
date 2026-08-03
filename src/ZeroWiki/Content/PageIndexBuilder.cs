using System.Text;
using Microsoft.Extensions.Logging;

namespace ZeroWiki.Content;

/// <summary>
/// Builds a <see cref="PageIndexSnapshot"/> from the repository (D6, D15) — the only place this change's
/// derived index gets constructed. Reuses <see cref="PageEnumerationService"/> for the walk (no second
/// walker), <see cref="PageFrontmatterExtractor"/>/<see cref="IFrontmatterParser"/> for title/tags, and
/// <see cref="PageHistoryService.GetAllLastEditsAsync"/> for last-edit metadata — one bulk <c>git log</c>
/// pass, not one per page.
/// </summary>
public sealed class PageIndexBuilder
{
    private readonly ContentPaths _paths;
    private readonly GitProcessRunner _git;
    private readonly PageEnumerationService _enumerationService;
    private readonly PageFrontmatterExtractor _frontmatterExtractor;
    private readonly PageHistoryService _historyService;
    private readonly ILogger<PageIndexBuilder> _logger;

    public PageIndexBuilder(
        ContentPaths paths,
        GitProcessRunner git,
        PageEnumerationService enumerationService,
        PageFrontmatterExtractor frontmatterExtractor,
        PageHistoryService historyService,
        ILogger<PageIndexBuilder> logger)
    {
        _paths = paths;
        _git = git;
        _enumerationService = enumerationService;
        _frontmatterExtractor = frontmatterExtractor;
        _historyService = historyService;
        _logger = logger;
    }

    /// <summary>
    /// Rebuilds the complete index from the repository: the current <c>HEAD</c>, a fresh enumeration of
    /// the working tree (D12, carrying its refusals into the snapshot unchanged), one bulk history walk
    /// (D15), and a bounded frontmatter read per page. A tree with no pages yields an otherwise-populated
    /// snapshot with an empty <see cref="PageIndexSnapshot.Pages"/> list; a build that could not be
    /// performed throws, naming what failed, rather than returning a silently empty index. An unborn
    /// <c>HEAD</c> — no commit exists yet — is D15's "no pages", not a failure, and short-circuits to
    /// <see cref="PageIndexSnapshot.Empty"/> without walking anything. This is also block 4.3's fallback:
    /// the full rebuild <see cref="RefreshAsync"/> reaches for when there is no previous stamp to diff
    /// from, or the previous stamp is one git can no longer resolve.
    /// </summary>
    public async Task<PageIndexSnapshot> BuildAsync(CancellationToken cancellationToken)
    {
        var commitSha = await ProbeCurrentHeadShaAsync(cancellationToken);
        if (commitSha is null)
        {
            return PageIndexSnapshot.Empty;
        }

        var enumeration = _enumerationService.EnumeratePages();
        var lastEdits = await _historyService.GetAllLastEditsAsync(cancellationToken);

        var pages = new List<PageIndexEntry>(enumeration.Pages.Count);
        foreach (var page in enumeration.Pages)
        {
            var frontmatter = await ReadFrontmatterAsync(page.AbsolutePath, cancellationToken);
            lastEdits.TryGetValue(page.RelativePath, out var lastEdit);

            pages.Add(new PageIndexEntry(
                page.Route,
                page.RelativePath,
                page.AbsolutePath,
                frontmatter.Title,
                frontmatter.Tags,
                lastEdit));
        }

        return new PageIndexSnapshot(pages, enumeration.AmbiguousRoutes, enumeration.UnreadableDirectories, commitSha);
    }

    /// <summary>
    /// The repository's current <c>HEAD</c> commit sha, or <see langword="null"/> for an unborn
    /// <c>HEAD</c> (no commit exists yet — D15's "no pages", not a failure). Shared by
    /// <see cref="BuildAsync"/> and by <see cref="PageIndex"/>'s freshness check (4.3), so the
    /// unborn-vs-genuine-failure distinction is made in exactly one place.
    /// </summary>
    /// <remarks>
    /// Same probe <see cref="ContentRepositoryService.RepositoryHeadIsUnbornAsync"/> already uses. <c>-q</c>
    /// only suppresses the error text for "no revision to resolve" (an unborn <c>HEAD</c>), which git
    /// reports with exit code 1; a repository fault git can detect before it even gets to resolving
    /// <c>HEAD</c> — e.g. "not a git repository" — exits 128 with its "fatal:" text intact regardless of
    /// <c>-q</c>. Only exit 1 means "no pages"; anything else is a genuine failure and must throw naming
    /// what failed, not be folded into the same silent empty-index outcome.
    /// </remarks>
    public async Task<string?> ProbeCurrentHeadShaAsync(CancellationToken cancellationToken)
    {
        var headProbeArguments = new[] { "rev-parse", "--verify", "-q", "HEAD" };
        var headProbe = await _git.RunAsync(
            _paths.RepositoryRoot,
            headProbeArguments,
            cancellationToken: cancellationToken);

        if (headProbe.ExitCode == 1)
        {
            return null;
        }

        if (!headProbe.Succeeded)
        {
            throw new GitProcessException(headProbeArguments, headProbe.ExitCode, headProbe.StandardError);
        }

        return headProbe.StandardOutput.Trim();
    }

    /// <summary>
    /// Brings <paramref name="current"/> up to date with <paramref name="currentHeadSha"/> (D15): an
    /// incremental re-index off <c>git diff --name-only</c>, touching only the entries the diff names,
    /// when that is possible — a full rebuild via <see cref="BuildAsync"/> when it is not (no previous
    /// stamp, i.e. <paramref name="current"/> is <see cref="PageIndexSnapshot.Empty"/>; a stamp git can no
    /// longer resolve because history was rewritten or the repository was reset behind the app's back; or
    /// <paramref name="current"/> has any <see cref="PageIndexSnapshot.UnreadableDirectories"/>, which
    /// means it is not a complete census of the tree and the incremental path's claimant reconstruction
    /// cannot be trusted — see the guard's own remarks below for why).
    /// Never called concurrently for the same <see cref="PageIndex"/> — <see cref="PageIndex.GetCurrentAsync"/>
    /// single-flights it, so this never races itself.
    /// </summary>
    public async Task<PageIndexSnapshot> RefreshAsync(
        PageIndexSnapshot current,
        string? currentHeadSha,
        CancellationToken cancellationToken)
    {
        if (string.Equals(current.CommitSha, currentHeadSha, StringComparison.Ordinal))
        {
            return current;
        }

        if (current.CommitSha is null || currentHeadSha is null)
        {
            // No previous stamp to diff from (Empty becoming born for the first time), or HEAD somehow
            // reporting unborn again having previously had a commit -- neither is expected to recur once
            // history exists, but both get the same honest answer: a full rebuild is always correct here,
            // never merely the faster option.
            return await BuildAsync(cancellationToken);
        }

        if (current.UnreadableDirectories.Count > 0)
        {
            // ApplyIncrementalUpdateAsync reconstructs an affected route's *other* claimants from
            // `current` itself (Pages + AmbiguousRoutes) -- sound only when `current` is a complete
            // census of every file outside the diff. It is not one of those when UnreadableDirectories is
            // non-empty: a file under a directory this process could not read at the last full build was
            // never recorded anywhere in the snapshot, so it is invisible to that reconstruction. If such
            // a directory later becomes readable -- a permissions change, not a git-tracked one, so no
            // `git diff` will ever name it -- and a path the diff *does* name happens to collide with a
            // file inside it, the incremental path would never see that second claimant and could serve a
            // route that does not identify it (D12) -- the exact wrong-file hazard §3 spent two supervisor
            // rounds preventing. A full rebuild is the only way to stay correct while any directory remains
            // unreadable; the cost lands only on a tree that is already in a state an operator should be
            // fixing anyway.
            return await BuildAsync(cancellationToken);
        }

        var diffArguments = new[]
        {
            "-c", "core.quotePath=false",
            "diff", "--no-renames", "--name-only", "-z",
            current.CommitSha, currentHeadSha,
            "--", _historyService.RepositoryRelativeWorkingTree,
        };

        var diffResult = await _git.RunAsync(_paths.RepositoryRoot, diffArguments, cancellationToken: cancellationToken);
        if (!diffResult.Succeeded)
        {
            // The stamped commit can no longer be resolved -- history rewritten, gc'd, or a
            // `reset --hard` behind the app's back. Detected honestly rather than folded into "nothing
            // changed", the mirror of BuildAsync's own unborn-vs-real-failure distinction: a failed diff
            // means "cannot tell what changed", not "nothing did".
            _logger.LogWarning(
                "git diff from the page index's stamped commit '{StampedSha}' to the current HEAD " +
                "'{CurrentSha}' failed (exit {ExitCode}: {StandardError}); rebuilding the index from " +
                "scratch instead of trusting a diff that could not be computed.",
                current.CommitSha,
                currentHeadSha,
                diffResult.ExitCode,
                diffResult.StandardError.Trim());
            return await BuildAsync(cancellationToken);
        }

        var affectedRepositoryPaths = diffResult.StandardOutput
            .Split('\x00')
            .Where(token => token.Length > 0)
            .ToList();

        if (affectedRepositoryPaths.Count == 0)
        {
            // HEAD moved, but nothing under the working tree changed between the two commits (e.g. a
            // commit touching only files outside docs/) -- only the stamp needs to move.
            return current with { CommitSha = currentHeadSha };
        }

        return await ApplyIncrementalUpdateAsync(current, currentHeadSha, affectedRepositoryPaths, cancellationToken);
    }

    /// <summary>
    /// Rebuilds only the routes touched by <paramref name="affectedRepositoryPaths"/>, reusing every
    /// other entry in <paramref name="current"/> unchanged. For each affected route, the route's other
    /// (unaffected) claimants come from <paramref name="current"/> itself — <see cref="PageIndexSnapshot.Pages"/>
    /// and <see cref="PageIndexSnapshot.AmbiguousRoutes"/> already record every claimant D12 knew about at
    /// the previous stamp — combined with a fresh on-disk check of the paths the diff actually names, so an
    /// addition or deletion can turn a route ambiguous or resolve an existing ambiguity without re-walking
    /// the whole tree (D15, D12's "refused routes survive indexing").
    /// </summary>
    private async Task<PageIndexSnapshot> ApplyIncrementalUpdateAsync(
        PageIndexSnapshot current,
        string newCommitSha,
        IReadOnlyList<string> affectedRepositoryPaths,
        CancellationToken cancellationToken)
    {
        var workingTreePrefix = _historyService.RepositoryRelativeWorkingTree + "/";

        // Same candidacy rules PageEnumerationService.Walk applies during a full walk (an ordinary,
        // lowercase-or-not ".md" file, no dot-prefixed segment) -- anything else was never a page
        // candidate before or after, so it cannot affect any route.
        var affectedRelativePaths = affectedRepositoryPaths
            .Where(path => path.StartsWith(workingTreePrefix, StringComparison.Ordinal))
            .Select(path => path[workingTreePrefix.Length..])
            .Where(relativePath =>
                relativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase) &&
                !HasDotPrefixedSegment(relativePath))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (affectedRelativePaths.Count == 0)
        {
            return current with { CommitSha = newCommitSha };
        }

        var affectedRoutes = affectedRelativePaths
            .Select(PageRouteCodec.Encode)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // The previous claimant set for every affected route, reconstructed from the current snapshot
        // rather than re-walked -- this is what lets an affected path be judged against claimants the
        // diff never mentioned (an unaffected file already sharing the same encoded route).
        var claimsByRoute = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var route in affectedRoutes)
        {
            claimsByRoute[route] = [];
        }

        foreach (var page in current.Pages)
        {
            if (claimsByRoute.TryGetValue(page.Route, out var claimants))
            {
                claimants.Add(page.RelativePath);
            }
        }

        foreach (var ambiguousRoute in current.AmbiguousRoutes)
        {
            if (claimsByRoute.TryGetValue(ambiguousRoute.Route, out var claimants))
            {
                claimants.AddRange(ambiguousRoute.RelativePaths);
            }
        }

        // Every affected path's membership is re-derived from the working tree, not trusted from the old
        // snapshot or from git's status letter: drop it from its route's claimant set first, then add it
        // back only if it currently exists as an ordinary file. A deleted path is simply never added back.
        var affectedPathSet = new HashSet<string>(affectedRelativePaths, StringComparer.Ordinal);
        foreach (var claimants in claimsByRoute.Values)
        {
            claimants.RemoveAll(affectedPathSet.Contains);
        }

        foreach (var relativePath in affectedRelativePaths)
        {
            if (StillExistsAsOrdinaryFile(relativePath))
            {
                claimsByRoute[PageRouteCodec.Encode(relativePath)].Add(relativePath);
            }
        }

        var affectedRoutesSet = new HashSet<string>(affectedRoutes, StringComparer.Ordinal);
        var updatedPages = current.Pages.Where(page => !affectedRoutesSet.Contains(page.Route)).ToList();
        var updatedAmbiguousRoutes = current.AmbiguousRoutes
            .Where(ambiguousRoute => !affectedRoutesSet.Contains(ambiguousRoute.Route))
            .ToList();

        foreach (var route in affectedRoutes)
        {
            var evaluation = PageEnumerationService.EvaluateRouteClaim(route, claimsByRoute[route]);

            if (evaluation.IsServable)
            {
                var relativePath = evaluation.RelativePath!;
                var absolutePath = Path.Combine(_paths.WorkingTree, relativePath.Replace('/', Path.DirectorySeparatorChar));
                var frontmatter = await ReadFrontmatterAsync(absolutePath, cancellationToken);
                var lastEdit = await _historyService.GetLastEditAsync(relativePath, cancellationToken);

                updatedPages.Add(new PageIndexEntry(route, relativePath, absolutePath, frontmatter.Title, frontmatter.Tags, lastEdit));
            }
            else if (evaluation.Claimants.Count > 0)
            {
                updatedAmbiguousRoutes.Add(new AmbiguousPageRoute(route, evaluation.Claimants));
            }

            // Neither branch: the route now has no claimant at all (its one file was deleted and nothing
            // else ever shared its route) -- it simply disappears from both lists, exactly as it would
            // never have gained an entry in a fresh PageEnumerationService.EnumeratePages() pass.
        }

        return new PageIndexSnapshot(updatedPages, updatedAmbiguousRoutes, current.UnreadableDirectories, newCommitSha);
    }

    /// <summary>
    /// Whether <paramref name="relativePath"/> currently exists on disk as an ordinary file this index
    /// could ever serve -- not a directory, not a symlink (matching <see cref="PageEnumerationService.Walk"/>'s
    /// reparse-point skip). A vanished, renamed-away, or newly-inaccessible path reads as "does not exist"
    /// rather than throwing -- the same race <see cref="PageEnumerationService"/>'s own walk and
    /// <c>WikiPage.razor</c>'s render path already tolerate.
    /// </summary>
    private bool StillExistsAsOrdinaryFile(string relativePath)
    {
        var absolutePath = Path.Combine(_paths.WorkingTree, relativePath.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            var attributes = File.GetAttributes(absolutePath);
            return (attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) == 0;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether any segment of <paramref name="relativePath"/> starts with <c>.</c> — matching
    /// <see cref="PageEnumerationService.Walk"/>'s skip of dot-prefixed files and directories (an Obsidian
    /// vault's <c>.obsidian/</c>), so a path git reports as changed inside such a directory is never
    /// treated as an affected page.
    /// </summary>
    private static bool HasDotPrefixedSegment(string relativePath) =>
        relativePath.Split('/').Any(segment => segment.StartsWith('.'));

    /// <summary>
    /// Reads at most <see cref="PageFrontmatterExtractor.MaxFrontmatterSizeBytes"/> <i>characters</i> —
    /// via <see cref="StreamReader"/>, never a raw byte slice, which could split a multi-byte UTF-8
    /// codepoint — from the head of <paramref name="absolutePath"/> and hands that prefix to
    /// <see cref="PageFrontmatterExtractor.Extract"/>. Frontmatter is always at the head of a file, so
    /// this is enough to contain a legal block without reading a page whole to extract two fields (D15).
    /// Sized from the bound <see cref="IFrontmatterParser"/>'s own cap rather than a second,
    /// independently-tuned number: any frontmatter block within that cap is well within this many
    /// characters (a UTF-8 character is never smaller than a byte), and a block that would exceed the cap
    /// is truncated here exactly as it would be truncated inside the parser itself, yielding
    /// <see cref="PageFrontmatter.Empty"/> by construction (D14's total-failure posture) — and if a future
    /// <see cref="IFrontmatterParser"/> is swapped in with a different cap, this read follows it rather
    /// than sizing itself from whichever implementation happened to ship first.
    /// </summary>
    /// <remarks>
    /// A file that vanishes or becomes unreadable between <see cref="PageEnumerationService.EnumeratePages"/>'s
    /// walk and this read — a concurrent write racing the read, the same class of race
    /// <c>WikiPage.razor</c>'s render path already tolerates — yields <see cref="PageFrontmatter.Empty"/>
    /// for that one page rather than failing the whole rebuild; the next rebuild sees whatever is there by
    /// then.
    /// </remarks>
    private async Task<PageFrontmatter> ReadFrontmatterAsync(string absolutePath, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                absolutePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            var buffer = new char[_frontmatterExtractor.MaxFrontmatterSizeBytes];
            var charsRead = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);

            return _frontmatterExtractor.Extract(new string(buffer, 0, charsRead));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex,
                "Could not read '{AbsolutePath}' while building the page index; it will have no " +
                "frontmatter metadata until the next rebuild.",
                absolutePath);
            return PageFrontmatter.Empty;
        }
    }
}
