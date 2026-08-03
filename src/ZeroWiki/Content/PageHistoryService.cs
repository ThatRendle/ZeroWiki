using System.Globalization;

namespace ZeroWiki.Content;

/// <summary>
/// Reads a page's authorship and last-edit time from git history (D5, 3.4) — <c>git log</c> is the only
/// source; there is no hand-maintained author field to fall back to.
/// </summary>
public sealed class PageHistoryService
{
    /// <summary>
    /// ASCII Unit Separator: never appears in an author's display name or an ISO-8601 date, so it is a
    /// safe delimiter between the two <c>%an</c>/<c>%aI</c> fields in a single <c>--format</c> line.
    /// </summary>
    private const char FieldSeparator = '\x1f';

    /// <summary>
    /// ASCII Start-of-Heading, prepended to every commit header line in <see cref="GetAllLastEditsAsync"/>'s
    /// <c>--format</c>. A git <c>--name-status</c> status code is always an ASCII letter (<c>A</c>/<c>M</c>/
    /// <c>D</c>/<c>T</c> here — copy/rename codes cannot appear because <c>--no-renames</c> is passed and
    /// copy detection is never requested), so this control character can never be mistaken for one; it is
    /// what lets the parser tell "this NUL-delimited token is a new commit's header" from "this token is a
    /// status code" without any lookahead.
    /// </summary>
    private const char HistoryHeaderMarker = '\x01';

    private readonly ContentPaths _paths;
    private readonly GitProcessRunner _git;
    private readonly ILogger<PageHistoryService> _logger;

    /// <summary>
    /// The working tree's path relative to the repository root (e.g. <c>docs</c>) — derived from
    /// <see cref="ContentPaths"/> rather than hardcoded (S3 lower-severity finding, block 3 remediation):
    /// a hardcoded <c>"docs/"</c> here was a second, independently-maintained spelling of the same fact
    /// <see cref="ContentPaths.WorkingTree"/> already owns, and if the two ever diverged, <c>git log</c>
    /// would silently return empty output for every page — indistinguishable from the legitimate
    /// "no history yet" case, since git does not treat an unmatched pathspec as an error. Deriving it
    /// removes the divergence this failure depended on rather than adding a second check to detect it.
    /// </summary>
    private readonly string _repositoryRelativeWorkingTree;

    public PageHistoryService(ContentPaths paths, GitProcessRunner git, ILogger<PageHistoryService> logger)
    {
        _paths = paths;
        _git = git;
        _logger = logger;
        _repositoryRelativeWorkingTree = Path
            .GetRelativePath(paths.RepositoryRoot, paths.WorkingTree)
            .Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>
    /// Returns the author and date of the most recent commit that touched
    /// <paramref name="workingTreeRelativePath"/>, or <see langword="null"/> if git has no history for it
    /// (an untracked file on disk, or the git subprocess itself failed) — a page still renders with no
    /// last-edit line rather than failing the whole request over metadata that isn't essential to it.
    /// </summary>
    /// <remarks>
    /// Always called with <see cref="CancellationToken.None"/> by its caller (3b), not the request's own
    /// token: <see cref="GitProcessRunner"/> does not yet kill the git subprocess on cancellation — that
    /// fix is owed to §6 — so passing a token here would imply a cancellation guarantee this does not
    /// have. Author date (<c>%aI</c>), not committer date: D5 ties "who edited this" to authorship, and a
    /// browser save stamps both identically (<see cref="GitAuthor.ToEnvironmentVariables"/>), so the two
    /// only ever diverge for a pushed commit, where author is the field D5 actually means.
    /// </remarks>
    public async Task<PageLastEdit?> GetLastEditAsync(string workingTreeRelativePath, CancellationToken cancellationToken)
    {
        var repositoryRelativePath = _repositoryRelativeWorkingTree + "/" +
            workingTreeRelativePath.Replace(Path.DirectorySeparatorChar, '/');

        GitProcessResult result;
        try
        {
            result = await _git.RunOrThrowAsync(
                _paths.RepositoryRoot,
                ["log", "-1", $"--format=%an{FieldSeparator}%aI", "--", repositoryRelativePath],
                cancellationToken: cancellationToken);
        }
        catch (GitProcessException ex)
        {
            _logger.LogWarning(
                ex,
                "git log failed for '{RelativePath}'; the page will render without last-edit metadata.",
                workingTreeRelativePath);
            return null;
        }

        var output = result.StandardOutput.TrimEnd('\n', '\r');
        if (output.Length == 0)
        {
            // No commit touches this path yet — an untracked file present on disk. Not an error: the
            // page still renders, just without a last-edit line.
            return null;
        }

        var fields = output.Split(FieldSeparator);
        if (fields.Length != 2
            || !DateTimeOffset.TryParse(
                fields[1],
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var editedAt))
        {
            _logger.LogWarning(
                "git log for '{RelativePath}' returned an unparseable line ({Output}); the page will " +
                "render without last-edit metadata.",
                workingTreeRelativePath,
                output);
            return null;
        }

        return new PageLastEdit(fields[0], editedAt);
    }

    /// <summary>
    /// Returns the most recent commit's author and date for <i>every</i> path git has ever recorded under
    /// the working tree, in one <c>git log --name-status</c> pass rather than one <c>git log</c> per page
    /// (D15) — a rebuild's cost scales with history walked once, not with page count times a process spawn.
    /// Keys are working-tree-relative paths using the platform directory separator, matching
    /// <see cref="EnumeratedPage.RelativePath"/>; a path git has touched that no longer exists on disk
    /// still gets an entry here, harmlessly unused by a caller that only looks up paths it just enumerated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>-z</c></b> — NUL-delimited output, immune to a path or author name that happens to contain
    /// what would otherwise be the line-based parser's delimiter. This is also what stops git quoting a
    /// non-ASCII byte in a path as a C-style octal escape (<c>"docs/Caf\303\251.md"</c>) in
    /// <c>--name-status</c> output — <c>core.quotePath</c>'s quoting behaviour is specific to the
    /// human-readable (non-<c>-z</c>) output format, confirmed empirically against a real git log with an
    /// accented filename: <c>-z</c> alone, with <c>core.quotePath</c> left at its default, already emits
    /// the raw UTF-8 bytes. §2 shipped a defect on exactly this quoting, against output that had no
    /// <c>-z</c> to suppress it.
    /// </para>
    /// <para>
    /// <b><c>-c core.quotePath=false</c></b> — redundant belt-and-braces given the above, kept anyway: it
    /// costs nothing, and it means this parser does not depend on <c>-z</c> remaining the only thing
    /// standing between it and quoted paths if this command line is ever edited.
    /// </para>
    /// <para>
    /// <b><c>--no-renames</c></b> — <c>diff.renames</c> defaults on, and a rename record under <c>-z</c>
    /// carries <i>two</i> NUL-delimited path fields (old, then new) for a single status code, which would
    /// silently misalign this parser's strict status/path alternation. With renames off, a rename is one
    /// delete record on the old path plus one add record on the new path — the add is exactly the correct
    /// last-edit for the file's current name, so nothing is lost by forcing this off.
    /// </para>
    /// <para>
    /// <b>Parsing the <c>-z</c> shape.</b> Splitting the whole output on NUL yields, per commit: one header
    /// token (this method's own <c>--format</c>, prefixed with <see cref="HistoryHeaderMarker"/> so it can
    /// never be mistaken for a status code), immediately followed by alternating status/path token pairs —
    /// except the very first status token after each header carries a stray leading <c>'\n'</c> (git's
    /// usual blank line between a commit's header and its file list, still emitted verbatim under
    /// <c>-z</c>), which is stripped before the token is inspected. Pathspec-filtering with
    /// <c>-- &lt;working tree&gt;</c> means every emitted commit is guaranteed to carry at least one file
    /// record, so a header is never immediately followed by another header.
    /// </para>
    /// <para>
    /// <b>Newest-first, first write wins.</b> <c>git log</c>'s default order is newest commit first, so
    /// the first time a path is seen it is already that path's most recent touch; every later sighting of
    /// the same path is discarded via <see cref="Dictionary{TKey,TValue}.TryAdd"/>.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, PageLastEdit>> GetAllLastEditsAsync(CancellationToken cancellationToken)
    {
        var result = await _git.RunOrThrowAsync(
            _paths.RepositoryRoot,
            [
                "-c", "core.quotePath=false",
                "log",
                "--no-renames",
                "--name-status",
                "-z",
                $"--format={HistoryHeaderMarker}%an{FieldSeparator}%aI",
                "--",
                _repositoryRelativeWorkingTree,
            ],
            cancellationToken: cancellationToken);

        var lastEdits = new Dictionary<string, PageLastEdit>(StringComparer.Ordinal);
        var repositoryPathPrefix = _repositoryRelativeWorkingTree + "/";

        string? currentAuthor = null;
        DateTimeOffset? currentEditedAt = null;

        var tokens = result.StandardOutput.Split('\x00');
        var index = 0;
        while (index < tokens.Length)
        {
            var token = tokens[index];
            index++;

            if (token.StartsWith('\n'))
            {
                token = token[1..];
            }

            if (token.Length == 0)
            {
                continue;
            }

            if (token[0] == HistoryHeaderMarker)
            {
                var fields = token[1..].Split(FieldSeparator);
                if (fields.Length == 2
                    && DateTimeOffset.TryParse(
                        fields[1],
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out var editedAt))
                {
                    currentAuthor = fields[0];
                    currentEditedAt = editedAt;
                }
                else
                {
                    // An unparseable header (should not happen against a real git log, but D14's total-
                    // failure posture applies here too): stop attributing until the next valid header.
                    currentAuthor = null;
                    currentEditedAt = null;
                }

                continue;
            }

            // token is a --name-status status code (A/M/D/T); the next token is its path.
            if (index >= tokens.Length)
            {
                break;
            }

            var repositoryRelativePath = tokens[index];
            index++;

            if (currentAuthor is null || currentEditedAt is null)
            {
                continue;
            }

            if (!repositoryRelativePath.StartsWith(repositoryPathPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var workingTreeRelativePath = repositoryRelativePath[repositoryPathPrefix.Length..]
                .Replace('/', Path.DirectorySeparatorChar);

            lastEdits.TryAdd(workingTreeRelativePath, new PageLastEdit(currentAuthor, currentEditedAt.Value));
        }

        return lastEdits;
    }
}
