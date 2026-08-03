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
    /// <see cref="PageIndexSnapshot.Empty"/> without walking anything.
    /// </summary>
    public async Task<PageIndexSnapshot> BuildAsync(CancellationToken cancellationToken)
    {
        // Same unborn-HEAD probe ContentRepositoryService already uses (RepositoryHeadIsUnbornAsync).
        // `-q` only suppresses the error text for "no revision to resolve" (an unborn HEAD), which git
        // reports with exit code 1; a repository fault git can detect before it even gets to resolving
        // HEAD — e.g. "not a git repository" — exits 128 with its "fatal:" text intact regardless of
        // -q. Only exit 1 means "no pages"; anything else is a genuine build failure and must throw
        // naming what failed, not be folded into the same silent empty-index outcome.
        var headProbeArguments = new[] { "rev-parse", "--verify", "-q", "HEAD" };
        var headProbe = await _git.RunAsync(
            _paths.RepositoryRoot,
            headProbeArguments,
            cancellationToken: cancellationToken);

        if (headProbe.ExitCode == 1)
        {
            return PageIndexSnapshot.Empty;
        }

        if (!headProbe.Succeeded)
        {
            throw new GitProcessException(headProbeArguments, headProbe.ExitCode, headProbe.StandardError);
        }

        var commitSha = headProbe.StandardOutput.Trim();

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
