using Microsoft.Extensions.Logging;

namespace ZeroWiki.Content;

/// <summary>
/// Detects or initializes the content git repository at <see cref="ContentPaths.RepositoryRoot"/> on
/// startup (D1, D8): an existing non-bare repository is used as-is, an empty or repo-less directory
/// is initialized, and a bare repository — incompatible with D1's working-tree requirement — fails
/// startup rather than being silently served as empty. Installs the Smart HTTP git hooks, reconciles
/// any dirty working tree left from a crash or manually copied-in content (D9), and asserts the
/// working-tree-clean invariant before returning.
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

    /// <summary>
    /// Message for the startup-reconciliation commit (D9). Deliberately distinct from
    /// <see cref="InitialCommitMessage"/> so an operator meeting it in <c>git log</c> with no other
    /// context recognises it as machine-made recovery, not an ordinary edit.
    /// </summary>
    private const string RecoveryCommitMessage = "Recover uncommitted content found at startup";

    private readonly ContentPaths _paths;
    private readonly GitProcessRunner _git;
    private readonly GitHookInstaller _hooks;
    private readonly ILogger<ContentRepositoryService> _logger;

    public ContentRepositoryService(
        ContentPaths paths,
        GitProcessRunner git,
        GitHookInstaller hooks,
        ILogger<ContentRepositoryService> logger)
    {
        _paths = paths;
        _git = git;
        _hooks = hooks;
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
        // if classification ever regresses, this is what stops the configuration/hooks writes below
        // from landing in a repository git merely discovered (e.g. an ancestor's), rather than
        // reporting that damage after it already happened.
        await AssertGitResolvesRepositoryRootAsync(repositoryRoot, cancellationToken);

        // Every refusal this method can raise — bare repository (above), missing docs/, and a staged
        // gitlink — happens before any write below. This ordering is load-bearing, not incidental
        // (design.md D9 addendum): the docs/ probe below needs only a HEAD read, and the gitlink probe
        // is intrinsically a staging-and-diff operation, so both can and must run before configuration
        // or hooks touch the repository at all. A repository this method is about to refuse never has
        // anything written to it first.
        await EnsureInitialCommitAsync(repositoryRoot, cancellationToken);

        // D9: a dirty tree at startup (e.g. Markdown copied onto the volume before first start, or an
        // interrupted save) is always committed as a recovery commit, never discarded.
        await ReconcileWorkingTreeAsync(repositoryRoot, cancellationToken);

        // Confirms reconciliation actually achieved a clean tree before anything further runs against
        // this repository.
        await AssertWorkingTreeIsCleanAsync(repositoryRoot, cancellationToken);

        // Runs last, now that every refusal above has had its chance to fire first: neither writes
        // anything a refusal above needs to be true of, and receive.denyCurrentBranch/http.receivepack
        // govern pushes while the hooks fire on push — neither can matter before the app is serving.
        await ApplyRepositoryConfigurationAsync(repositoryRoot, cancellationToken);
        await _hooks.InstallHooksAsync(repositoryRoot, cancellationToken);
    }

    private static InvalidOperationException BareRepositoryException(string repositoryRoot) =>
        new(
            $"The content repository root '{repositoryRoot}' is a bare git repository. " +
            "ZeroWiki requires a non-bare repository with a working tree; refusing to start.");

    private InvalidOperationException MissingWorkingTreeException(string repositoryRoot) =>
        new(
            $"The content repository at '{repositoryRoot}' has commit history but no " +
            $"'{_paths.WorkingTree}' working tree. ZeroWiki does not write to a repository it did " +
            "not itself initialize until it has accepted it, and a repository missing its working " +
            "tree is never accepted. Create the 'docs' directory at the repository root (it can be " +
            "empty) and restart the application.");

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

    /// <summary>
    /// On a freshly initialized repository (no <c>HEAD</c> yet), creates <c>docs/</c> and commits it.
    /// On a repository that already has history — one this call did not create, whether a previous
    /// start of this app or a repository adopted from elsewhere — does neither: if
    /// <see cref="ContentPaths.WorkingTree"/> is absent, refuses to start rather than silently
    /// establish one (Product Owner decision), and does so before <see cref="EnsureRepositoryAsync"/>
    /// has written any configuration or hooks — the same before-any-write posture as the
    /// bare-repository and gitlink refusals elsewhere in this class.
    /// </summary>
    private async Task EnsureInitialCommitAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        var headProbe = await _git.RunAsync(
            repositoryRoot,
            ["rev-parse", "--verify", "-q", "HEAD"],
            cancellationToken: cancellationToken);

        if (headProbe.Succeeded)
        {
            // Already has history — this call did not create the repository, or a previous start did.
            // Either way, ZeroWiki does not create docs/ here: doing so on an adopted repository would
            // be establishing a working tree, and possibly committing, into history it did not create.
            if (!Directory.Exists(_paths.WorkingTree))
            {
                throw MissingWorkingTreeException(repositoryRoot);
            }

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

    /// <summary>
    /// D9: commits any uncommitted changes left in the working tree at startup — for example after a
    /// crash mid-save, or a folder of Markdown copied onto the volume before the app's first start —
    /// as a single recovery commit authored <see cref="GitAuthor.System"/>. Always commits, never
    /// discards, with no configurable policy: discarding is unrecoverable, and an unwanted recovery
    /// commit is a plain <c>git revert</c> — except for a nested repository, which this method refuses
    /// to commit at all rather than silently gitlink (see <see cref="FindStagedGitlinksAsync"/>).
    /// </summary>
    /// <remarks>
    /// The gitlink check (<see cref="FindStagedGitlinksAsync"/>) asks whether committing now would
    /// *introduce* a gitlink not already in <c>HEAD</c> — a delta, not a census of the whole index —
    /// so a gitlink that landed in history before this check existed does not refuse startup on every
    /// later restart. Detecting one retroactively is explicitly out of scope (Product Owner decision):
    /// no shipped build has ever created one, and doing so would refuse to start a wiki that already
    /// has one in its history, which is a worse outcome than leaving the pre-existing gap undetected.
    /// </remarks>
    private async Task ReconcileWorkingTreeAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        // `add -A` stages tracked modifications and deletions *and* untracked files. Untracked files
        // are the load-bearing case: copying a folder of Markdown onto the volume is the ordinary way
        // to populate a new ZeroWiki, and it arrives untracked. Staging only tracked changes (e.g.
        // `add -u`) would leave that content uncommitted — the tree still dirty, and every push
        // bouncing — while every existing check up to this point still looks like it succeeded.
        await _git.RunOrThrowAsync(repositoryRoot, ["add", "-A"], cancellationToken: cancellationToken);

        // D9 addendum (Product Owner decision): if any of what was just staged is itself a nested git
        // repository — an operator copying in an existing Obsidian vault or a cloned notes folder,
        // .git and all — `add -A` does not stage its files. It stages the directory as a gitlink (mode
        // 160000), a bare reference to a commit in an object database this repository never touches.
        // A commit would "succeed", git status --porcelain would report clean, and the invariant
        // assertion below would pass — while the actual file contents are not recoverable from this
        // repository's history at all, which defeats D9's own "an unwanted recovery commit is a plain
        // git revert" rationale. Refuse to start rather than commit a gitlink silently.
        var gitlinkPaths = await FindStagedGitlinksAsync(repositoryRoot, cancellationToken);
        if (gitlinkPaths.Count > 0)
        {
            // Unstage before throwing, so a refused start is not also a half-staged one — `git reset`
            // resets the index back to HEAD without touching the working tree, leaving exactly what
            // the operator copied in untouched for them to fix.
            await _git.RunOrThrowAsync(repositoryRoot, ["reset"], cancellationToken: cancellationToken);

            throw new InvalidOperationException(BuildGitlinkErrorMessage(repositoryRoot, gitlinkPaths));
        }

        var stagedDiff = await _git.RunAsync(
            repositoryRoot,
            ["diff", "--cached", "--quiet"],
            cancellationToken: cancellationToken);

        if (stagedDiff.Succeeded)
        {
            // Nothing was staged: the tree was already clean. D9: a clean tree produces no commit.
            return;
        }

        await _git.RunOrThrowAsync(
            repositoryRoot,
            ["commit", "-m", RecoveryCommitMessage],
            environmentVariables: GitAuthor.System.ToEnvironmentVariables(),
            cancellationToken: cancellationToken);

        _logger.LogWarning(
            "Startup reconciliation found uncommitted changes in the content repository at " +
            "'{RepositoryRoot}' and committed them as a recovery commit authored 'System " +
            "<system@zerowiki.org>' (D9).",
            repositoryRoot);
    }

    /// <summary>
    /// Finds every path that committing the current index right now would record as a <em>newly
    /// introduced</em> gitlink (mode <c>160000</c>) — the entry git writes for a nested repository
    /// instead of its file contents. Deliberately narrower than "every gitlink the index carries": an
    /// adopted repository may legitimately already have one (a submodule, or any pre-guard means), and
    /// that entry advancing its own nested <c>HEAD</c> is the normal way for it to change, not a fault.
    /// </summary>
    /// <remarks>
    /// Asks <c>git diff --cached --raw</c> — a diff of the index against <c>HEAD</c> — rather than
    /// <c>git ls-files --stage</c>, which lists the *entire* index regardless of whether any of it is
    /// new. A gitlink that already exists in <c>HEAD</c> produces either no diff entry at all (unchanged)
    /// or an <c>M</c> record whose <em>old</em> mode is already <c>160000</c> (its nested repository
    /// merely advanced) — both must be ignored, or an adopted repository containing a legitimate
    /// submodule would start fine once and then refuse forever the moment that submodule's pointer
    /// moves, which is exactly the bricking this check exists to prevent, arriving through the one
    /// status (<c>M</c>, not <c>A</c>) an earlier version of this method did not distinguish. A gitlink
    /// genuinely being introduced now is an <c>A</c> record (<c>old mode 000000</c>) or, in principle,
    /// a <c>T</c> (typechange) record for a previously tracked path replaced by a nested repository —
    /// both have <c>old mode != 160000</c>, which is the actual condition below (it tests modes, not
    /// status letters, so this correction does not change behaviour).
    /// <para>
    /// This call always sees a <em>born</em> <c>HEAD</c> — <see cref="EnsureInitialCommitAsync"/> runs
    /// unconditionally before reconciliation in <see cref="EnsureRepositoryAsync"/> and either creates
    /// the initial commit or refuses to start when history exists with no working tree — so the
    /// genuinely unborn-<c>HEAD</c> case (a fresh repository with no commits at all) never actually
    /// reaches this method or the <c>git reset</c> below it. (As a property of git, <c>diff --cached
    /// --raw</c> *would* diff against the empty tree if it ever did face an unborn <c>HEAD</c> — but
    /// that is not why this code is safe today, and would only become load-bearing if a future change
    /// reordered <c>EnsureRepositoryAsync</c> to run reconciliation before the initial commit.)
    /// </para>
    /// <para>
    /// Uses <c>-z</c>: without it, <c>core.quotePath</c> (on by default) renders any non-ASCII path as
    /// a quoted C-style octal escape (e.g. <c>café-vault</c> becomes <c>"caf\303\251-vault"</c>), which
    /// would land verbatim in the exception message an operator is meant to act on — defeating the
    /// point of naming the path. <c>-z</c> disables that quoting and NUL-delimits records instead of
    /// newline-delimiting them, so records and path fields are split on <c>'\0'</c>, not <c>'\n'</c>.
    /// Each record is <c>":&lt;old mode&gt; &lt;new mode&gt; &lt;old sha&gt; &lt;new sha&gt;
    /// &lt;status&gt;"</c>, NUL, then the path, NUL — except a rename/copy record (status <c>R</c>/
    /// <c>C</c>), which carries two NUL-terminated paths (old name, then new name); the new name is
    /// what this repository would actually contain, so it is the one kept.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<string>> FindStagedGitlinksAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        var diff = await _git.RunOrThrowAsync(
            repositoryRoot,
            ["diff", "--cached", "--raw", "-z"],
            cancellationToken: cancellationToken);

        var fields = diff.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var gitlinkPaths = new List<string>();

        var index = 0;
        while (index < fields.Length)
        {
            var metadataParts = fields[index].TrimStart(':').Split(' ');
            index++;

            var oldMode = metadataParts[0];
            var newMode = metadataParts[1];
            var status = metadataParts[4];

            // A rename/copy record carries two NUL-terminated paths (old name, then new name); every
            // other status carries one. Keep the last one consumed — the name this repository would
            // actually hold.
            var pathFieldCount = status.Length > 0 && (status[0] == 'R' || status[0] == 'C') ? 2 : 1;
            var path = string.Empty;
            for (var i = 0; i < pathFieldCount && index < fields.Length; i++, index++)
            {
                path = fields[index];
            }

            // newMode alone is not enough: an M record for a gitlink whose nested HEAD merely advanced
            // also has newMode 160000, and that is not this reconciliation introducing anything — it is
            // an already-adopted gitlink changing the way gitlinks normally do. Requiring oldMode to
            // differ excludes exactly that case while still catching a genuinely new gitlink (an A
            // record, oldMode 000000) or a previously tracked path replaced by one.
            if (newMode == "160000" && oldMode != "160000")
            {
                gitlinkPaths.Add(path);
            }
        }

        return gitlinkPaths;
    }

    private static string BuildGitlinkErrorMessage(string repositoryRoot, IReadOnlyList<string> gitlinkPaths)
    {
        var offendingPaths = string.Join(", ", gitlinkPaths.Select(path => $"'{path}'"));

        return
            $"The content repository at '{repositoryRoot}' contains a nested git repository at " +
            $"{offendingPaths} — most likely copied in with its own .git directory (for example an " +
            "existing Obsidian vault, or a cloned notes folder). ZeroWiki cannot commit a nested " +
            "repository's file contents into this repository: git would record only a reference to a " +
            "commit in the nested repository's own history, not the files themselves, and that " +
            "content would not be recoverable from this wiki if the nested .git is ever removed. " +
            "Remove the nested .git (or move the folder's contents in without it) and restart the " +
            "application; refusing to start rather than commit a broken reference.";
    }

    /// <summary>
    /// Asserts the working-tree-clean invariant holds after reconciliation. A startup-only check with
    /// no HTTP surface (Product Owner decision): an anonymous endpoint would leak repository state to
    /// strangers, an authenticated one cannot be called by an unauthenticated Docker
    /// <c>HEALTHCHECK</c>, and the runtime image has no HTTP client to call one with anyway. A
    /// non-empty result means reconciliation failed to do its job, not merely that the tree happens to
    /// be dirty, so the failure message says that rather than just reporting uncleanliness.
    /// </summary>
    private async Task AssertWorkingTreeIsCleanAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        var status = await _git.RunOrThrowAsync(
            repositoryRoot,
            ["status", "--porcelain"],
            cancellationToken: cancellationToken);

        if (!string.IsNullOrEmpty(status.StandardOutput))
        {
            throw new InvalidOperationException(
                $"The content repository at '{repositoryRoot}' is not clean after startup " +
                $"reconciliation (git status --porcelain reported):\n{status.StandardOutput}" +
                "Reconciliation should have committed every change; refusing to start rather than " +
                "accept pushes against a dirty tree.");
        }
    }
}
