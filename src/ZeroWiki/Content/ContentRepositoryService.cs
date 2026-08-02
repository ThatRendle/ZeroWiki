using Microsoft.Extensions.Logging;

namespace ZeroWiki.Content;

/// <summary>
/// Detects or initializes the content git repository at <see cref="ContentPaths.RepositoryRoot"/> on
/// startup (D1, D8): an existing non-bare repository is used as-is, an empty or repo-less directory
/// is initialized, and a bare repository — incompatible with D1's working-tree requirement — fails
/// startup rather than being silently served as empty.
/// </summary>
/// <remarks>
/// Deliberately not <c>BootstrapService</c> — <see cref="ZeroWiki.Identity.BootstrapService"/>
/// already owns that name for the first-administrator flow.
/// </remarks>
public sealed class ContentRepositoryService
{
    /// <summary>
    /// The branch every content repository is initialized on. Named explicitly rather than left to
    /// <c>git init</c>'s default: bare <c>git init</c> takes <c>init.defaultBranch</c> from host
    /// configuration, which differs between a developer's laptop and the container, and the Smart
    /// HTTP remote's <c>updateInstead</c> push targets whichever branch is checked out.
    /// </summary>
    public const string DefaultBranch = "main";

    private const string InitialCommitMessage = "Initial commit";

    private readonly ContentPaths _paths;
    private readonly GitProcessRunner _git;
    private readonly ILogger<ContentRepositoryService> _logger;

    public ContentRepositoryService(ContentPaths paths, GitProcessRunner git, ILogger<ContentRepositoryService> logger)
    {
        _paths = paths;
        _git = git;
        _logger = logger;
    }

    /// <summary>
    /// Ensures <see cref="ContentPaths.RepositoryRoot"/> is a usable non-bare git repository with the
    /// configuration the Smart HTTP remote needs, applying that configuration on every call — not
    /// only when this call is the one that initializes the repository — so a repository made by an
    /// older image, or restored from a backup, receives it too.
    /// </summary>
    public async Task EnsureRepositoryAsync(CancellationToken cancellationToken = default)
    {
        var repositoryRoot = _paths.RepositoryRoot;
        Directory.CreateDirectory(repositoryRoot);

        if (HasOwnGitEntry(repositoryRoot))
        {
            // repositoryRoot has its own .git — git's discovery, run with this as the working
            // directory, will find this entry first regardless of any ancestor repository, so it is
            // safe to ask git directly from here on.
            var bareProbe = await _git.RunOrThrowAsync(
                repositoryRoot,
                ["rev-parse", "--is-bare-repository"],
                cancellationToken: cancellationToken);

            if (bareProbe.StandardOutput.Trim() == "true")
            {
                throw BareRepositoryException(repositoryRoot);
            }

            _logger.LogInformation("Existing non-bare git repository detected at '{RepositoryRoot}'.", repositoryRoot);
        }
        else if (LooksLikeBareGitDirectory(repositoryRoot))
        {
            // A bare repository has no .git entry of its own — HEAD/objects/refs sit directly in
            // repositoryRoot, which is exactly what makes it bare. This has to be recognised in C#
            // too, or it falls through to "no repository, initialize" and git init happily layers a
            // working tree's worth of state onto an existing bare repository's directory.
            throw BareRepositoryException(repositoryRoot);
        }
        else
        {
            // No .git of its own, and not a bare repository directly at repositoryRoot either.
            // Deciding "no repository" from git itself (e.g. `rev-parse --is-bare-repository`) would
            // be wrong here: git's discovery walks up into parent directories looking for one, so a
            // repositoryRoot nested inside an unrelated ancestor repository (e.g. a relative
            // ContentStorage:DataRoot resolved under a developer's own checkout) would report the
            // ancestor's repository as "found" instead of "absent". Both checks above are answered
            // entirely in C#, before any git process runs, so this branch is only ever taken when
            // repositoryRoot truly has no repository of its own.
            _logger.LogInformation("No git repository found at '{RepositoryRoot}'; initializing.", repositoryRoot);
            await _git.RunOrThrowAsync(
                repositoryRoot,
                ["init", "-b", DefaultBranch],
                cancellationToken: cancellationToken);
        }

        // Every branch above that did not throw leaves .git present at repositoryRoot — found as-is,
        // or just created by init — so the assertion has what it needs here. Run it before any write:
        // if classification ever regresses, this is what stops ApplyRepositoryConfigurationAsync from
        // writing receive.denyCurrentBranch/http.receivepack into a repository git merely discovered
        // (e.g. an ancestor's), rather than reporting that damage after it already happened.
        await AssertGitResolvesRepositoryRootAsync(repositoryRoot, cancellationToken);

        await ApplyRepositoryConfigurationAsync(repositoryRoot, cancellationToken);
        await EnsureInitialCommitAsync(repositoryRoot, cancellationToken);
    }

    private static InvalidOperationException BareRepositoryException(string repositoryRoot) =>
        new(
            $"The content repository root '{repositoryRoot}' is a bare git repository. " +
            "ZeroWiki requires a non-bare repository with a working tree; refusing to start.");

    /// <summary>
    /// Whether <paramref name="repositoryRoot"/> has a <c>.git</c> entry of its own — a directory for
    /// an ordinary repository, or a gitfile for a worktree/submodule layout. Either means "a repository
    /// is already here"; a gitfile is deliberately not treated as "absent", since initializing over a
    /// worktree or submodule would destroy its linkage to its real git directory.
    /// </summary>
    private static bool HasOwnGitEntry(string repositoryRoot)
    {
        var gitEntryPath = Path.Combine(repositoryRoot, ".git");
        return Directory.Exists(gitEntryPath) || File.Exists(gitEntryPath);
    }

    /// <summary>
    /// Whether <paramref name="repositoryRoot"/> itself looks like a bare git directory: no
    /// <c>.git</c> entry (a bare repository's git directory <em>is</em> its root), but the markers a
    /// bare <c>git init</c> produces are present directly inside it.
    /// </summary>
    private static bool LooksLikeBareGitDirectory(string repositoryRoot) =>
        File.Exists(Path.Combine(repositoryRoot, "HEAD")) &&
        Directory.Exists(Path.Combine(repositoryRoot, "objects")) &&
        Directory.Exists(Path.Combine(repositoryRoot, "refs"));

    /// <summary>
    /// Defense-in-depth for every later git invocation this runner makes with
    /// <c>WorkingDirectory = repositoryRoot</c> (§3.4 <c>log</c>/<c>blame</c>, §6 commit-on-save, §7
    /// the Smart HTTP remote): confirms that plain git repository discovery — run exactly the way
    /// every other caller runs it, with no <c>--git-dir</c> override — resolves to
    /// <paramref name="repositoryRoot"/> itself rather than some ancestor repository.
    /// </summary>
    /// <remarks>
    /// Compares two git-resolved paths rather than a git-resolved path against a C# one:
    /// <c>rev-parse --show-toplevel</c> returns a symlink-resolved real path, and
    /// <see cref="Path.GetFullPath"/> does not resolve symlinks at all. A naive comparison against
    /// <paramref name="repositoryRoot"/> would misfire on any host whose data root sits behind a
    /// symlink — confirmed by reproduction: on macOS, the default temp directory used by this
    /// project's own tests resolves <c>/tmp/&#8230;</c> to <c>/private/tmp/&#8230;</c>, which a
    /// literal string comparison would treat as a mismatch even though the repository is correctly
    /// resolved. Forcing an explicit <c>--git-dir</c>/<c>--work-tree</c> (bypassing discovery
    /// entirely) and comparing its output to plain discovery's sidesteps that: both sides go through
    /// git's own realpath resolution, so they are directly comparable regardless of symlinks.
    /// </remarks>
    private async Task AssertGitResolvesRepositoryRootAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        var expected = await _git.RunOrThrowAsync(
            repositoryRoot,
            ["--git-dir=.git", "--work-tree=.", "rev-parse", "--show-toplevel"],
            cancellationToken: cancellationToken);

        var discovered = await _git.RunOrThrowAsync(
            repositoryRoot,
            ["rev-parse", "--show-toplevel"],
            cancellationToken: cancellationToken);

        var expectedToplevel = expected.StandardOutput.Trim();
        var discoveredToplevel = discovered.StandardOutput.Trim();

        if (!string.Equals(expectedToplevel, discoveredToplevel, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"git's repository discovery from '{repositoryRoot}' resolved to " +
                $"'{discoveredToplevel}' instead of the content repository itself " +
                $"('{expectedToplevel}'). Refusing to start rather than operate on the wrong repository.");
        }
    }

    private async Task ApplyRepositoryConfigurationAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        // D1: the Smart HTTP remote accepts fast-forward pushes into the checked-out branch.
        await _git.RunOrThrowAsync(
            repositoryRoot,
            ["config", "receive.denyCurrentBranch", "updateInstead"],
            cancellationToken: cancellationToken);

        // Without this, git-receive-pack refuses with 403 — git's own export policy, not
        // authentication — which a later push-handling section would otherwise misdiagnose as an
        // auth failure.
        await _git.RunOrThrowAsync(
            repositoryRoot,
            ["config", "http.receivepack", "true"],
            cancellationToken: cancellationToken);
    }

    private async Task EnsureInitialCommitAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        var headProbe = await _git.RunAsync(
            repositoryRoot,
            ["rev-parse", "--verify", "-q", "HEAD"],
            cancellationToken: cancellationToken);

        if (headProbe.Succeeded)
        {
            // Already has history — this call did not create the repository, or a previous start did.
            return;
        }

        Directory.CreateDirectory(_paths.WorkingTree);

        // git does not track empty directories; without a tracked file here, a fresh Obsidian clone
        // would have no docs/ at all.
        var gitKeepPath = Path.Combine(_paths.WorkingTree, ".gitkeep");
        if (!File.Exists(gitKeepPath))
        {
            await File.WriteAllTextAsync(gitKeepPath, string.Empty, cancellationToken);
        }

        await _git.RunOrThrowAsync(
            repositoryRoot,
            ["add", "docs/.gitkeep"],
            cancellationToken: cancellationToken);

        await _git.RunOrThrowAsync(
            repositoryRoot,
            ["commit", "-m", InitialCommitMessage],
            environmentVariables: GitAuthor.System.ToEnvironmentVariables(),
            cancellationToken: cancellationToken);

        _logger.LogInformation("Initial commit created for the content repository at '{RepositoryRoot}'.", repositoryRoot);
    }
}
