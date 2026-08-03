using Microsoft.Extensions.Logging;

namespace ZeroWiki.Content;

/// <summary>
/// Walks <see cref="ContentPaths.WorkingTree"/> once and produces every page's route (3.1, D12). Grouping
/// by route is free here — the whole tree is already being walked — so D12's per-route invariant is
/// checked in the same pass rather than as a second scan.
/// </summary>
/// <remarks>
/// <para>
/// <b>A route is served only when it identifies exactly one file</b> — one property, not two independent
/// checks (D12): exactly one file claims the route, <i>and</i> <see cref="PageRouteCodec.TryDecode"/>
/// applied to that route reproduces that file's own relative path. Checking claimant count alone is
/// necessary but not sufficient — a route with a single claimant whose own decode disagrees with it (e.g.
/// <c>Chapter  1.md</c>, two spaces, whose route <c>Chapter__1</c> decodes to <c>Chapter_1.md</c>) is
/// refused exactly like a route two files both claim, because either way a later save inverting the route
/// could land on a file other than the one being read.
/// </para>
/// <para>
/// <b>Dot-prefixed files and directories are skipped</b> — an Obsidian vault carries <c>.obsidian/</c>,
/// and dot-prefixed is an established hidden-file convention rather than an invented namespace. This is
/// unrelated to the <c>_</c>-prefix namespace question the Product Owner has explicitly left open (D12's
/// prose) — that namespace is not implemented here.
/// </para>
/// <para>
/// <b>Symlinks are never followed</b> — matching <see cref="ContentRepositoryService"/>'s pre-init scan
/// (<c>AssertNoNestedGitRepository</c>), for the same two reasons: a symlink can loop back on an ancestor
/// of itself, and it can point outside the volume entirely. Applied uniformly to both symlinked
/// directories and symlinked files: the architect's brief called out directory symlinks specifically
/// (matching the git-nesting scan, which only ever needed to reason about directories), but a symlinked
/// <c>.md</c> file carries the same "may point outside the volume" hazard as a symlinked directory, and
/// enumeration has no narrower reason to trust one kind of reparse point over the other.
/// </para>
/// <para>
/// <b>An unreadable subdirectory is reported, not silently skipped</b> — silently missing pages is the
/// degradation pattern this change refuses everywhere else, so a directory this process cannot read is
/// recorded in <see cref="PageEnumerationResult.UnreadableDirectories"/> and every page inside it is
/// simply absent from <see cref="PageEnumerationResult.Pages"/>, exactly like a route-level collision.
/// Deliberately not a startup refusal: unlike <see cref="ContentRepositoryService"/>'s pre-init scan —
/// permissions on a volume an operator has just finished setting up, checked once before anything is
/// served — page content arrives continuously at runtime by push, so refusing to start the whole wiki
/// over one unreadable subtree would let a single misconfigured directory brick every other page too.
/// The proportionate response is the same one D12 uses for an ambiguous route: name the fault, keep
/// serving everything else.
/// </para>
/// </remarks>
public sealed class PageEnumerationService
{
    private readonly ContentPaths _paths;
    private readonly ILogger<PageEnumerationService> _logger;

    public PageEnumerationService(ContentPaths paths, ILogger<PageEnumerationService> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public PageEnumerationResult EnumeratePages()
    {
        var claimsByRoute = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var unreadableDirectories = new List<string>();

        if (Directory.Exists(_paths.WorkingTree))
        {
            Walk(_paths.WorkingTree, _paths.WorkingTree, claimsByRoute, unreadableDirectories);
        }

        var pages = new List<EnumeratedPage>();
        var ambiguousRoutes = new List<AmbiguousPageRoute>();

        foreach (var (route, relativePaths) in claimsByRoute.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            // D12's invariant is one property, not two alternatives: "the route identifies exactly this
            // file" — exactly one file claims the route, AND decoding the route reproduces that file's
            // own path. Checking claimant count alone misses the case that has no second file at all:
            // "Chapter  1.md" (two spaces) is the tree's only claimant of "Chapter__1", yet that route
            // decodes to "Chapter_1.md" — a file that does not exist. Serving it would let a later save
            // resolve the route to a different file than the one being read.
            var identifiesExactlyOneFile =
                relativePaths.Count == 1 &&
                PageRouteCodec.TryDecode(route, out var roundTrippedPath) &&
                string.Equals(roundTrippedPath, relativePaths[0], StringComparison.Ordinal);

            if (identifiesExactlyOneFile)
            {
                var relativePath = relativePaths[0];
                var absolutePath = Path.Combine(_paths.WorkingTree, ToPlatformPath(relativePath));
                pages.Add(new EnumeratedPage(route, relativePath, absolutePath));
            }
            else
            {
                var claimants = relativePaths.OrderBy(path => path, StringComparer.Ordinal).ToArray();
                ambiguousRoutes.Add(new AmbiguousPageRoute(route, claimants));

                _logger.LogWarning(
                    "Route '{Route}' does not identify exactly one file ({Count} claimant(s): {Claimants}); " +
                    "refusing to serve it (D12).",
                    route,
                    claimants.Length,
                    string.Join(", ", claimants));
            }
        }

        if (unreadableDirectories.Count > 0)
        {
            _logger.LogWarning(
                "{Count} subdirectory(ies) under the content working tree could not be read and were " +
                "skipped entirely: {Directories}. Pages inside them are not served.",
                unreadableDirectories.Count,
                string.Join(", ", unreadableDirectories));
        }

        return new PageEnumerationResult(pages, ambiguousRoutes, unreadableDirectories);
    }

    private static void Walk(
        string workingTreeRoot,
        string directory,
        Dictionary<string, List<string>> claimsByRoute,
        List<string> unreadableDirectories)
    {
        List<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(directory).ToList();
        }
        catch (UnauthorizedAccessException)
        {
            unreadableDirectories.Add(ToRoutePath(Path.GetRelativePath(workingTreeRoot, directory)));
            return;
        }

        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            if (name.StartsWith('.'))
            {
                continue;
            }

            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                Walk(workingTreeRoot, entry, claimsByRoute, unreadableDirectories);
                continue;
            }

            if (!name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relativePath = ToRoutePath(Path.GetRelativePath(workingTreeRoot, entry));
            var route = PageRouteCodec.Encode(relativePath);

            if (!claimsByRoute.TryGetValue(route, out var claimants))
            {
                claimants = [];
                claimsByRoute[route] = claimants;
            }

            claimants.Add(relativePath);
        }
    }

    private static string ToRoutePath(string path) => path.Replace(Path.DirectorySeparatorChar, '/');

    private static string ToPlatformPath(string path) => path.Replace('/', Path.DirectorySeparatorChar);
}
