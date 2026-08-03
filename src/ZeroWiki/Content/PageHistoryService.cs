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

    private readonly ContentPaths _paths;
    private readonly GitProcessRunner _git;
    private readonly ILogger<PageHistoryService> _logger;

    public PageHistoryService(ContentPaths paths, GitProcessRunner git, ILogger<PageHistoryService> logger)
    {
        _paths = paths;
        _git = git;
        _logger = logger;
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
        var repositoryRelativePath = "docs/" + workingTreeRelativePath.Replace(Path.DirectorySeparatorChar, '/');

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
}
