using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    private readonly TimeSpan _writeLockTimeout;

    public ContentRepositoryService(
        ContentPaths paths,
        GitProcessRunner git,
        GitHookInstaller hooks,
        ILogger<ContentRepositoryService> logger,
        IOptions<ContentStorageOptions> options)
    {
        _paths = paths;
        _git = git;
        _hooks = hooks;
        _logger = logger;
        _writeLockTimeout = options.Value.WriteLockTimeout;
    }

    /// <summary>
    /// Ensures <see cref="ContentPaths.RepositoryRoot"/> is a usable non-bare git repository with the
    /// configuration the Smart HTTP remote needs, applying that configuration on every call — not
    /// only when this call is the one that initializes the repository — so a repository made by an
    /// older image, or restored from a backup, receives it too.
    /// </summary>
    /// <remarks>
    /// Composed of two phases, split along the boundary D16's write lock needs (§5.2):
    /// <see cref="AcceptRepositoryAsync"/> — directory creation, classification, the nested-repository
    /// and gitlink refusals, and the reconciliation commit — holds <see cref="RepositoryWriteLock"/> for
    /// the whole phase, since it is the phase that needs mutual exclusion with a concurrent push;
    /// <see cref="ConfigureRepositoryAsync"/> — <c>receive.denyCurrentBranch</c>/<c>http.receivepack</c>
    /// and hook installation — does not take the lock, since neither writes tracked content and a push
    /// cannot arrive before the repository has already been configured at least once.
    /// </remarks>
    public async Task EnsureRepositoryAsync(CancellationToken cancellationToken = default)
    {
        await AcceptRepositoryAsync(cancellationToken);
        await ConfigureRepositoryAsync(cancellationToken);
    }

    /// <summary>
    /// The accept-and-write phase (D16): detects or initializes the repository at
    /// <see cref="ContentPaths.RepositoryRoot"/>, reconciles any dirty working tree left from a crash
    /// or manually copied-in content (D9), and asserts the working-tree-clean invariant. Every refusal
    /// this method can raise — bare repository, a nested repository (scanned before any commit exists
    /// to diff against, and again via the staged-gitlink check once one does), and a missing working
    /// tree on an adopted repository — happens before any write (design.md D9 addendum); this ordering
    /// is load-bearing, not incidental, and this method must not perturb it.
    /// </summary>
    /// <remarks>
    /// Holds <see cref="RepositoryWriteLock"/> for the entire method, acquired before directory creation
    /// or classification and released only once this method returns or throws (D16, <c>## NEXT</c>
    /// obligation 16): a second app instance starting concurrently against the same data volume (a
    /// rolling deploy's overlap) blocks here until the first instance's own accept phase completes,
    /// rather than being able to classify the repository, wait on the lock, and then act on a now-stale
    /// <c>repositoryHasNoCommitsYet</c>. That predicate is therefore (re-)derived after the lock is
    /// acquired every time this method runs, never trusted as a value that could have been computed
    /// before it.
    /// </remarks>
    private async Task AcceptRepositoryAsync(CancellationToken cancellationToken)
    {
        var repositoryRoot = _paths.RepositoryRoot;

        // RepositoryWriteLock.LockFilePath is a sibling of repositoryRoot, directly under DataRoot
        // (D16) — production always has this directory already (it is the mounted volume itself), but
        // a fresh test fixture may not, and creating it is not a write to repository state, so it does
        // not need to happen under the lock.
        Directory.CreateDirectory(_paths.DataRoot);

        using var writeLock = await AcquireStartupWriteLockAsync(cancellationToken);

        Directory.CreateDirectory(repositoryRoot);

        var hasOwnGitEntry = HasOwnGitEntry(repositoryRoot);
        bool repositoryHasNoCommitsYet;

        if (hasOwnGitEntry)
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

            // .git existing here does not mean a commit exists: a process that died between `git init`
            // and the initial commit (or a `.git` created some other way) leaves exactly this shape —
            // .git present, HEAD unborn. EnsureInitialCommitAsync below is about to write the initial
            // commit into this repository in that case, so it needs the same nested-repository scan a
            // freshly-initialized repository gets, before that write happens.
            repositoryHasNoCommitsYet = await RepositoryHeadIsUnbornAsync(repositoryRoot, cancellationToken);
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
            // repositoryRoot truly has no repository of its own — and trivially has no commits either,
            // since nothing has been written here at all yet.
            repositoryHasNoCommitsYet = true;

            _logger.LogInformation("No git repository found at '{RepositoryRoot}'; initializing.", repositoryRoot);
        }

        // The single predicate that governs whether ZeroWiki is about to write the initial commit into
        // this repository — computed once above, true whether .git does not exist yet or it does but
        // HEAD never advanced past `git init`. An earlier version of this method gated the scan on "no
        // .git entry" alone, which is a *different* condition that only coincides with this one on a
        // fresh volume; the moment .git exists without a commit in it, the two diverge, and that
        // divergence is exactly what let a nested repository through undetected. Gating on this single,
        // shared value — also passed into EnsureInitialCommitAsync below — is what keeps them from
        // drifting apart again. A folder copied onto the volume before ZeroWiki's first start can
        // already contain a nested git repository (e.g. a copied-in Obsidian vault); the
        // reconciliation-time gitlink check further down can only see that once something has been
        // staged, which requires a commit to diff against, so this scan is what catches it before then.
        //
        // git init lives inside this same block, immediately after the scan, rather than behind a
        // second top-level `if (!hasOwnGitEntry)` further down: the predicate that decides whether to
        // initialize (`!hasOwnGitEntry`, true only when this branch's own classification chose it) and
        // the write it governs belong together, and a reader should not have to hold two `if`s apart in
        // the method body to see why a repository was initialized here.
        if (repositoryHasNoCommitsYet)
        {
            AssertNoNestedGitRepository(repositoryRoot);

            if (!hasOwnGitEntry)
            {
                // Scanning above happens before this call: leaving nothing behind if it refuses is the
                // entire point of running the scan first on this branch.
                await _git.RunOrThrowAsync(
                    repositoryRoot,
                    ["init", "-b", DefaultBranch],
                    cancellationToken: cancellationToken);
            }
        }

        // Every branch above that did not throw leaves .git present at repositoryRoot — found as-is,
        // or just created by init — so the assertion has what it needs here. Run it before any write:
        // if classification ever regresses, this is what stops the initial-commit/reconciliation writes
        // below from landing in a repository git merely discovered (e.g. an ancestor's), rather than
        // reporting that damage after it already happened.
        await AssertGitResolvesRepositoryRootAsync(repositoryRoot, cancellationToken);

        // D17, §6 block D4 continuation round three: must run before any `add` below, on every call to
        // this method — not deferred to ConfigureRepositoryAsync, which runs only once this whole
        // method returns and would therefore leave every startup's own ReconcileWorkingTreeAsync (which
        // runs unconditionally, not only on first init) still exposed to the exact warning this exists
        // to prevent. See ApplyContentSafetyConfigurationAsync's own remarks for why.
        await ApplyContentSafetyConfigurationAsync(repositoryRoot, cancellationToken);

        // The docs/ probe below needs only the already-computed repositoryHasNoCommitsYet value, and
        // the gitlink probe (inside ReconcileWorkingTreeAsync) is intrinsically a staging-and-diff
        // operation — both can and must run before this repository is ever configured or has hooks
        // installed. A repository this method is about to refuse never has anything written to it
        // first.
        await EnsureInitialCommitAsync(repositoryRoot, repositoryHasNoCommitsYet, cancellationToken);

        // D9: a dirty tree at startup (e.g. Markdown copied onto the volume before first start, or an
        // interrupted save) is always committed as a recovery commit, never discarded.
        await ReconcileWorkingTreeAsync(repositoryRoot, cancellationToken);

        // Confirms reconciliation actually achieved a clean tree before anything further runs against
        // this repository.
        await AssertWorkingTreeIsCleanAsync(repositoryRoot, cancellationToken);
    }

    /// <summary>
    /// Acquires <see cref="RepositoryWriteLock"/> for <see cref="AcceptRepositoryAsync"/>, bounded by
    /// <see cref="ContentStorageOptions.WriteLockTimeout"/>.
    /// </summary>
    /// <remarks>
    /// D16 settles what a <em>bounded save</em> (§6, not yet built) does when its own wait for this
    /// lock expires — a per-request "repository busy" failure, not one of this class's fatal startup
    /// refusals. It does not settle what <em>startup's</em> accept phase should do on the same timeout,
    /// since there is no request here to fail cleanly instead. This method's answer: refuse to start,
    /// the same posture this class already takes for any repository state it cannot safely proceed with
    /// (D9) — the app's own bound (10s by default) comfortably covers realistic contention (D16), so
    /// hitting it here means something is genuinely stuck, not ordinary overlap.
    /// </remarks>
    private async Task<RepositoryWriteLock> AcquireStartupWriteLockAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await RepositoryWriteLock.AcquireAsync(_paths.LockFilePath, _writeLockTimeout, cancellationToken);
        }
        catch (RepositoryLockTimeoutException ex)
        {
            throw new InvalidOperationException(
                $"Could not acquire the repository write lock at '{ex.LockFilePath}' within " +
                $"{ex.Timeout}. This most likely means another ZeroWiki instance's own startup is still " +
                "holding it against the same data volume (e.g. a rolling deploy's brief overlap), which " +
                "resolves itself once that instance finishes — restarting is not needed in that case. " +
                "It can also mean the app's own wrapper around a git push's git http-backend invocation " +
                "is stuck holding the lock indefinitely (by design, its wait for this lock has no bound " +
                "— never a receive hook, which must not acquire this lock itself). Refusing to start " +
                "rather than proceed without exclusive access to the repository.",
                ex);
        }
    }

    /// <summary>
    /// The configure phase (D16): applies the Smart HTTP remote's git configuration and installs the
    /// push hooks. Runs only once <see cref="AcceptRepositoryAsync"/> has completed without refusing —
    /// neither write here needs to be true of a repository <see cref="AcceptRepositoryAsync"/> would
    /// have refused, and <c>receive.denyCurrentBranch</c>/<c>http.receivepack</c> govern pushes while
    /// the hooks fire on push, neither of which can matter before the app is serving. Needs no mutual
    /// exclusion with a concurrent push: neither write touches tracked content, so it sits outside
    /// D16's write lock.
    /// </summary>
    private async Task ConfigureRepositoryAsync(CancellationToken cancellationToken)
    {
        var repositoryRoot = _paths.RepositoryRoot;

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
    /// D17: reconciliation's <c>git add -A</c> printed something on stderr this app does not recognise
    /// as safe to ignore, even though the process exited successfully. Refusing here (rather than
    /// committing whatever was staged and reporting success) is what keeps D9's "never discard" holding
    /// for whatever that stderr describes.
    /// </summary>
    /// <remarks>
    /// The message deliberately does not guess <em>what</em> went wrong beyond what git itself said
    /// (D17, §6 block D4 continuation round three, a Product Owner-reported production defect). An
    /// earlier version of this exception assumed every such stderr meant an unreadable directory and
    /// told the operator to fix permissions — correct for that one cause, but this app has since learned
    /// of another (a benign CRLF line-ending warning under an inherited <c>core.autocrlf</c> setting,
    /// now removed at the source by <see cref="ApplyContentSafetyConfigurationAsync"/>) that stderr
    /// alone cannot be told apart from at this call site without pattern-matching git's own,
    /// locale-dependent message text. A refusal that misdiagnoses its cause and prescribes the wrong fix
    /// is worse than one that honestly says "something unexpected happened, here is exactly what git
    /// reported" and lets the operator judge it themselves — the verbatim <paramref name="standardError"/>
    /// is what makes that judgement possible.
    /// </remarks>
    private static InvalidOperationException UnexpectedReconciliationStderrException(string repositoryRoot, string standardError) =>
        new(
            $"Startup reconciliation of the content repository at '{repositoryRoot}' saw 'git add -A' " +
            "report something on stderr that this app does not recognise as safe to ignore, even though " +
            "the command itself exited successfully. Refusing to start rather than assume it is " +
            "harmless: content behind whatever it describes could be silently left out of the recovery " +
            $"commit while every later check still reports a clean tree.\n\nWhat git reported:\n{standardError.Trim()}\n\n" +
            "This is not necessarily a permissions problem — read the message above for what git " +
            "actually said, resolve it, and restart the application.");

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
    /// Refuses if <paramref name="repositoryRoot"/>'s directory tree contains a nested git repository —
    /// a <c>.git</c> directory or gitfile at any depth <em>below</em> <paramref name="repositoryRoot"/>
    /// itself (its own top-level <c>.git</c>, if it already has one, is never itself "nested" — see
    /// <see cref="CollectNestedGitEntries"/>). Called by <see cref="AcceptRepositoryAsync"/> whenever
    /// the repository has no commits yet — before it creates the initial commit, whether that is
    /// because <c>.git</c> does not exist yet (before <c>git init</c> runs) or because it does but
    /// <c>HEAD</c> is unborn (e.g. a process that died between <c>git init</c> and the initial commit
    /// that should have followed it). <see cref="FindStagedGitlinksAsync"/> answers the same underlying
    /// worry from git's index once a commit exists to diff against; this scan exists because there is no
    /// index yet in either case here — nothing has been staged, because nothing has been written yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is a second instrument, deliberately, not a duplicate of <see cref="FindStagedGitlinksAsync"/>.</b>
    /// This scan asks "is there a nested repository in this folder", walking the filesystem directly,
    /// and guards <see cref="AcceptRepositoryAsync"/> only when it is about to create the initial
    /// commit, before any write. <see cref="FindStagedGitlinksAsync"/> asks "would committing right now
    /// store a gitlink", from git's index, and guards every reconciliation once a commit already exists
    /// — including content that arrives long after bootstrap, on a repository this scan never runs
    /// against again. Neither subsumes the other.
    /// </para>
    /// <para>
    /// The two deliberately disagree in one case (Product Owner decision, recorded in
    /// <c>design.md</c>'s D9 addendum): a nested repository the operator has <c>.gitignore</c>d is
    /// skipped entirely by <c>git add -A</c>, so <see cref="FindStagedGitlinksAsync"/> never sees it —
    /// while this scan does not consult <c>.gitignore</c> at all, so it still finds the <c>.git</c>
    /// entry and refuses. That divergence is intentional: refusing is recoverable and legible, and a
    /// gitignored nested repository inside a wiki's content folder is a configuration worth stopping
    /// on rather than silently accepting.
    /// </para>
    /// <para>
    /// Does not follow symbolic links while walking: a symlinked directory can loop back on an
    /// ancestor or point outside the volume entirely, and neither is a nested repository in this
    /// folder. <see cref="File.GetAttributes(string)"/> reports <see cref="FileAttributes.ReparsePoint"/>
    /// for a symlink alongside <see cref="FileAttributes.Directory"/> when it targets one, without this
    /// method ever having to open or descend into it — so checking that flag is enough to recognise and
    /// skip a symlink before recursing into whatever it targets.
    /// </para>
    /// <para>
    /// <b>An unreadable subdirectory is a refusal, not a silent skip.</b> This scan exists to assert a
    /// property — "no nested repository anywhere in this tree" — before any write, and on a
    /// subdirectory this process cannot read, that property is <em>unverifiable</em>, not merely
    /// unverified. Treating "couldn't check" as "checked, clean" would silently reintroduce the exact
    /// failure mode the rest of this section refuses loudly instead: a repository could be hiding
    /// behind restrictive permissions and this scan would report the tree clean anyway. The cost of
    /// refusing here is low — once per volume, on content the operator has just copied in, most likely
    /// while they are still watching — so an unreadable directory refuses by name, with a message
    /// distinct from the nested-repository one: fixing permissions and removing a nested repository are
    /// different actions, and the operator needs to know which one applies.
    /// </para>
    /// </remarks>
    private static void AssertNoNestedGitRepository(string repositoryRoot)
    {
        var nestedGitPaths = new List<string>();
        var unreadableDirectories = new List<string>();
        CollectNestedGitEntries(repositoryRoot, repositoryRoot, nestedGitPaths, unreadableDirectories);

        // Checked first: an unreadable subtree makes "no nested repository anywhere" unverifiable, not
        // merely unverified, regardless of what the readable parts of the tree happened to show.
        if (unreadableDirectories.Count > 0)
        {
            throw new InvalidOperationException(BuildUnreadableDirectoryErrorMessage(repositoryRoot, unreadableDirectories));
        }

        if (nestedGitPaths.Count > 0)
        {
            throw new InvalidOperationException(BuildNestedRepositoryScanErrorMessage(repositoryRoot, nestedGitPaths));
        }
    }

    private static void CollectNestedGitEntries(
        string repositoryRoot,
        string directory,
        List<string> found,
        List<string> unreadableDirectories)
    {
        List<string> entries;
        try
        {
            // Materialized inside the try: this scan's enumeration must not treat a permission denial
            // encountered mid-walk any differently from one encountered at the call itself.
            entries = Directory.EnumerateFileSystemEntries(directory).ToList();
        }
        catch (UnauthorizedAccessException)
        {
            unreadableDirectories.Add(RelativePath(repositoryRoot, directory));
            return;
        }

        foreach (var entry in entries)
        {
            var attributes = File.GetAttributes(entry);

            // Never follow a symlink: it may loop back on an ancestor of itself, or point outside the
            // volume to something that is not nested in this folder at all.
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            if (Path.GetFileName(entry) == ".git")
            {
                // At repositoryRoot's own top level, a .git entry is this repository's own git
                // directory (or gitfile) — relevant now that this scan also runs against an already-
                // -adopted repository whose HEAD is unborn (see AssertNoNestedGitRepository's remarks).
                // Only a .git found inside a subdirectory is nested.
                if (!string.Equals(directory, repositoryRoot, StringComparison.Ordinal))
                {
                    found.Add(RelativePath(repositoryRoot, entry));
                }

                continue;
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                CollectNestedGitEntries(repositoryRoot, entry, found, unreadableDirectories);
            }
        }
    }

    private static string RelativePath(string repositoryRoot, string path) =>
        Path.GetRelativePath(repositoryRoot, path).Replace(Path.DirectorySeparatorChar, '/');

    private static string BuildUnreadableDirectoryErrorMessage(string repositoryRoot, IReadOnlyList<string> unreadableDirectories)
    {
        var offendingPaths = string.Join(", ", unreadableDirectories.Select(path => $"'{path}'"));

        return
            $"The content directory at '{repositoryRoot}' contains a subdirectory this process cannot " +
            $"read at {offendingPaths}, before it has ever been initialized as a git repository. " +
            "ZeroWiki cannot confirm there is no nested git repository hiding inside an unreadable " +
            "directory, and refusing to start is safer than assuming it is clean. Fix the directory's " +
            "permissions so this process can read it (not the same problem as a nested repository — no " +
            "\".git\" needs to be removed here) and restart the application.";
    }

    private static string BuildNestedRepositoryScanErrorMessage(string repositoryRoot, IReadOnlyList<string> nestedGitPaths)
    {
        var offendingPaths = string.Join(", ", nestedGitPaths.Select(path => $"'{path}'"));

        return
            $"The content directory at '{repositoryRoot}' already contains a nested git repository at " +
            $"{offendingPaths} — most likely copied in with its own .git directory (for example an " +
            "existing Obsidian vault, or a cloned notes folder). ZeroWiki cannot commit a nested " +
            "repository's file contents into the repository it is about to create here: git would " +
            "record only a reference to a commit in the nested repository's own history, not the files " +
            "themselves, and that content would not be recoverable from this wiki if the nested .git is " +
            "ever removed. Remove the nested .git (or move the folder's contents in without it) and " +
            "restart the application; refusing to start rather than initialize a repository around a " +
            "nested one.";
    }

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
    /// Pins <c>core.autocrlf=false</c> on the content repository (D17, §6 block D4 continuation round
    /// three) — a Product Owner decision forced by an availability defect found in production, every
    /// link of it executed rather than reasoned about: an HTML <c>&lt;textarea&gt;</c> submits CRLF
    /// regardless of what the member typed; <c>git add -A</c> then warns on stderr about the conversion
    /// it would perform "the next time git touches" that file, whenever the <em>host's</em> inherited
    /// git configuration sets <c>core.autocrlf=input</c> (or <c>true</c>) and git's own index stat cache
    /// happens to be cold for that path — which a fresh clone, a container restart, or a remounted
    /// volume all produce; and <see cref="ReconcileWorkingTreeAsync"/>'s own guard refuses to start on
    /// <em>any</em> such stderr (Product Owner decision, reaffirmed rather than weakened when this exact
    /// defect surfaced: no silent content loss, ever). An inherited setting this app never chose, and
    /// cannot see from its own configuration, could therefore brick startup on perfectly ordinary
    /// content — "point it at a folder and it Just Works" cannot depend on the operator's own
    /// <c>~/.gitconfig</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This removes the class rather than teaching the guard to forgive one instance of it: with no
    /// conversion attempted, <c>add -A</c> produces no stderr for a CRLF-bearing file at all — verified
    /// by execution in a scratch repository (inherited <c>autocrlf=input</c>: warns; the same file with
    /// <c>autocrlf=false</c> set on the repository: no stderr whatsoever, because there is nothing left
    /// to warn about). Bytes are then stored exactly as <see cref="PageSaveService.NormalizeLineEndings"/>
    /// already writes them (LF-only) — the honest posture for a repository this app calls the source of
    /// truth: git should not silently rewrite a member's bytes on top of what the save path already
    /// normalized, and a push arriving with CRLF from an editor that does not normalize is stored
    /// exactly as sent rather than mutated on the way in.
    /// </para>
    /// <para>
    /// Deliberately <em>not</em> folded into <see cref="ApplyRepositoryConfigurationAsync"/> alongside
    /// <c>receive.denyCurrentBranch</c>/<c>http.receivepack</c>, even though that is the more obviously
    /// parallel location for "repository configuration this app pins": that method runs from
    /// <see cref="ConfigureRepositoryAsync"/>, which <see cref="EnsureRepositoryAsync"/> calls only
    /// <em>after</em> <see cref="AcceptRepositoryAsync"/> has already returned. Setting this pin there
    /// would leave every startup's own <see cref="ReconcileWorkingTreeAsync"/> call — which runs
    /// unconditionally inside <see cref="AcceptRepositoryAsync"/>, on every single start, not only the
    /// first — still exposed to the exact warning this pin exists to prevent, since the pin would not
    /// yet be in effect when that call runs. This method is called from <see cref="AcceptRepositoryAsync"/>
    /// itself instead, immediately after <see cref="AssertGitResolvesRepositoryRootAsync"/> confirms
    /// <c>.git</c> is resolvable and before any <c>add</c> in this class runs.
    /// </para>
    /// </remarks>
    private async Task ApplyContentSafetyConfigurationAsync(string repositoryRoot, CancellationToken cancellationToken) =>
        await _git.RunOrThrowAsync(
            repositoryRoot,
            ["config", "core.autocrlf", "false"],
            cancellationToken: cancellationToken);

    /// <summary>
    /// Whether <paramref name="repositoryRoot"/>'s <c>HEAD</c> is unborn: <c>.git</c> exists, but no
    /// commit has ever been made in this repository — for example a process that died between
    /// <c>git init</c> and the initial commit that should have followed it. Only ever called once
    /// <c>.git</c> is already confirmed present at <paramref name="repositoryRoot"/> itself (see
    /// <see cref="HasOwnGitEntry"/>), for the same reason every other git invocation in this class
    /// requires that confirmation first: without it, discovery could resolve to an unrelated ancestor
    /// repository instead.
    /// </summary>
    /// <remarks>
    /// D17: matches the *specific documented* exit code rather than <c>!Succeeded</c>. <c>git
    /// rev-parse --verify -q HEAD</c> exits <b>1</b> when <c>HEAD</c> does not resolve to a commit and
    /// <b>128</b> on "not a git repository" or any other resolution fault — folding both into "unborn"
    /// would read a genuine repository fault as the ordinary fresh-repository case and silently proceed
    /// to write into it.
    /// <para>
    /// D17's second clause: matching that documented code correctly still only answers "does
    /// <c>HEAD</c> resolve", not "does this repository have any history" — the question this method's
    /// caller actually needs answered. Git does not distinguish a genuinely fresh repository from one
    /// whose <c>HEAD</c> is a symbolic ref pointing at a branch that was deleted or never existed (e.g.
    /// a botched rename, or a lost ref file): both make exit 1 with empty stderr, and no refinement of
    /// exit-code handling can tell them apart. So exit 1 is followed by a second, output-based probe —
    /// <c>git for-each-ref --count=1 --format='%(refname)'</c> — which answers by *output*, consulting
    /// no exit code, exactly D17's posture: empty output means no ref exists anywhere, genuinely fresh;
    /// any ref means real history exists somewhere and this is a repository fault, refused rather than
    /// silently treated as fresh (which would otherwise make <see cref="EnsureInitialCommitAsync"/>
    /// create an orphan root commit on the phantom branch, disconnecting the wiki's real history from
    /// everything ZeroWiki reads while every later check still reports success). Deliberately
    /// <c>for-each-ref --count=1</c> rather than <c>git rev-list --all --count</c>: the former answers
    /// in O(1) against the ref database, the latter would walk the whole commit history to answer a
    /// question that does not need it, on the startup path of every boot.
    /// </para>
    /// Same first probe and same shape as <see cref="PageIndexBuilder.ProbeCurrentHeadShaAsync"/>,
    /// which answers "does HEAD resolve" for the read path; kept as one posture rather than two. That
    /// method does not need the second probe below — this method's refusal keeps the read path from
    /// ever observing a dangling-symref repository at all, so the stronger guarantee lives here once
    /// rather than being duplicated on the read path.
    /// </remarks>
    private async Task<bool> RepositoryHeadIsUnbornAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        var headProbeArguments = new[] { "rev-parse", "--verify", "-q", "HEAD" };
        var headProbe = await _git.RunAsync(
            repositoryRoot,
            headProbeArguments,
            cancellationToken: cancellationToken);

        if (headProbe.ExitCode == 1)
        {
            var refProbeArguments = new[] { "for-each-ref", "--count=1", "--format=%(refname)" };
            var refProbe = await _git.RunOrThrowAsync(
                repositoryRoot,
                refProbeArguments,
                cancellationToken: cancellationToken);

            if (string.IsNullOrWhiteSpace(refProbe.StandardOutput))
            {
                return true;
            }

            var symbolicRefProbe = await _git.RunAsync(
                repositoryRoot,
                ["symbolic-ref", "HEAD"],
                cancellationToken: cancellationToken);
            var headTarget = symbolicRefProbe.Succeeded
                ? symbolicRefProbe.StandardOutput.Trim()
                : "a detached, unresolvable commit";

            // `--count=1` stops at the first ref, so this names one example rather than an inventory:
            // the refusal only needs to establish that the repository is not empty, and walking every
            // ref to build a complete list would answer a question nobody asked at startup cost.
            throw new InvalidOperationException(
                $"The content repository at '{repositoryRoot}' has an unresolvable HEAD (it points at " +
                $"{headTarget}, which does not resolve to a commit) but is not a genuinely fresh " +
                $"repository — at least one ref (for example {refProbe.StandardOutput.Trim()}) still " +
                "exists and may hold real history. Refusing to start rather than create a new initial " +
                "commit on the unresolvable branch, which would silently orphan that history. Repoint " +
                "HEAD at the branch holding the intended history and restart.");
        }

        if (!headProbe.Succeeded)
        {
            throw new GitProcessException(headProbeArguments, headProbe.ExitCode, headProbe.StandardError);
        }

        return false;
    }

    /// <summary>
    /// On a repository with no commits yet (<paramref name="repositoryHasNoCommitsYet"/>), creates
    /// <c>docs/</c> and commits it. On a repository that already has a commit — one this call did not
    /// create, whether a previous start of this app or a repository adopted from elsewhere — does
    /// neither: if <see cref="ContentPaths.WorkingTree"/> is absent, refuses to start rather than
    /// silently establish one (Product Owner decision), and does so before
    /// <see cref="EnsureRepositoryAsync"/> has written any configuration or hooks — the same
    /// before-any-write posture as the bare-repository and gitlink refusals elsewhere in this class.
    /// </summary>
    /// <param name="repositoryHasNoCommitsYet">
    /// Computed once by <see cref="AcceptRepositoryAsync"/> and passed in rather than re-derived here,
    /// so it is the same value that also gates <see cref="AssertNoNestedGitRepository"/> — the two
    /// decisions share one predicate and cannot drift apart.
    /// </param>
    private async Task EnsureInitialCommitAsync(
        string repositoryRoot,
        bool repositoryHasNoCommitsYet,
        CancellationToken cancellationToken)
    {
        if (!repositoryHasNoCommitsYet)
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

        // The obsidian-git plugin's vault config directory. The vault is opened at the repository
        // root and at docs/ (D3), so the unanchored pattern below must match .obsidian/ at either
        // level; this is seeded only into the initial commit (D1) and never untracks an existing
        // repository's own history (D2).
        //
        // A commit-less volume can still carry a hand-written .gitignore -- content copied on before
        // the app's first start (D5). That file is adopted, not overwritten: append `.obsidian/`
        // unless one of its existing lines already *is* that line, trimmed of surrounding whitespace.
        // Matching a whole line, never a substring, is the point -- a commented-out
        // `# .obsidian/ is deliberately tracked` does not count as the rule being present.
        const string ObsidianIgnoreRule = ".obsidian/";
        var gitIgnorePath = Path.Combine(repositoryRoot, ".gitignore");
        if (!File.Exists(gitIgnorePath))
        {
            await File.WriteAllTextAsync(gitIgnorePath, ObsidianIgnoreRule + "\n", cancellationToken);
        }
        else
        {
            var existingContent = await File.ReadAllTextAsync(gitIgnorePath, cancellationToken);
            var alreadyPresent = existingContent
                .Split('\n')
                .Any(line => line.Trim() == ObsidianIgnoreRule);

            if (!alreadyPresent)
            {
                var needsSeparatingNewline = existingContent.Length > 0 && !existingContent.EndsWith('\n');
                var appendix = (needsSeparatingNewline ? "\n" : string.Empty) + ObsidianIgnoreRule + "\n";

                // Append, never overwrite (D2/D5): the operator's rules survive into the initial
                // commit alongside ours. A missing trailing newline on the operator's last line is
                // completed above so appending cannot fold our rule onto the end of theirs.
                await File.AppendAllTextAsync(gitIgnorePath, appendix, cancellationToken);
            }
        }

        await _git.RunOrThrowAsync(
            repositoryRoot,
            ["add", "docs/.gitkeep", ".gitignore"],
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
    /// <para>
    /// This index-based check is not this repository's only nested-repository guard:
    /// <see cref="AcceptRepositoryAsync"/> also runs <see cref="AssertNoNestedGitRepository"/>, a
    /// filesystem scan, whenever the repository has no commits yet — before it creates the initial
    /// commit — because there is no index yet at that point for this check to see. See
    /// <see cref="AssertNoNestedGitRepository"/>'s remarks for exactly which cases that covers, why both
    /// checks stay, and for the one case where they deliberately disagree.
    /// </para>
    /// </remarks>
    private async Task ReconcileWorkingTreeAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        // `add -A` stages tracked modifications and deletions *and* untracked files. Untracked files
        // are the load-bearing case: copying a folder of Markdown onto the volume is the ordinary way
        // to populate a new ZeroWiki, and it arrives untracked. Staging only tracked changes (e.g.
        // `add -u`) would leave that content uncommitted — the tree still dirty, and every push
        // bouncing — while every existing check up to this point still looks like it succeeded.
        //
        // D17: `add -A` exits 0 even when it has something to warn about — an unreadable directory is
        // one cause (it stages whatever it *could* read and warns about the rest, so an unreadable
        // subtree would be silently unstaged while every later check, this method's own diff below and
        // AssertWorkingTreeIsCleanAsync, still reports success), and a CRLF line-ending conversion under
        // an inherited core.autocrlf setting is another, found in production (§6 block D4 continuation
        // round three) — this app cannot enumerate every message git might ever put on this stderr, so
        // the posture is "any stderr refuses", not "known-bad stderr refuses" (Product Owner decision:
        // no silent content loss, ever). `RunOrThrowAsync` only inspects the exit code, so it cannot
        // catch this; called via `RunAsync` here instead, with stderr inspected explicitly below,
        // deliberately local to this call site rather than a change to `RunOrThrowAsync` itself —
        // tightening stderr handling globally would silently re-gate every other caller
        // (PageHistoryService's `git log`, the index builder's probes) with faults this design never
        // intended to catch there. Reproduced on git 2.55.0 (macOS, dev) and 2.43.0 (Ubuntu 24.04, the
        // shipped image): both exit 0 with `warning: could not open directory '<path>/': Permission
        // denied` on stderr for an unreadable directory, and both produce empty stderr for `add -A` on a
        // healthy tree — `ApplyContentSafetyConfigurationAsync`'s own remarks cover the CRLF case, which
        // this app now removes at the source rather than trying to also recognise here.
        var addArguments = new[] { "add", "-A" };
        var addResult = await _git.RunAsync(repositoryRoot, addArguments, cancellationToken: cancellationToken);
        if (!addResult.Succeeded)
        {
            throw new GitProcessException(addArguments, addResult.ExitCode, addResult.StandardError);
        }

        // D9 addendum (Product Owner decision): if any of what was just staged is itself a nested git
        // repository — an operator copying in an existing Obsidian vault or a cloned notes folder,
        // .git and all — `add -A` does not stage its files. It stages the directory as a gitlink (mode
        // 160000), a bare reference to a commit in an object database this repository never touches.
        // A commit would "succeed", git status --porcelain would report clean, and the invariant
        // assertion below would pass — while the actual file contents are not recoverable from this
        // repository's history at all, which defeats D9's own "an unwanted recovery commit is a plain
        // git revert" rationale. Refuse to start rather than commit a gitlink silently.
        //
        // Deliberately checked before the stderr check below, not after: staging a gitlink is *also*
        // one of the cases that puts text on `add -A`'s stderr — git 2.55 emits an "adding embedded
        // git repository" advisory hint alongside the warning this block exists to catch — and this
        // check is the more specific, already-established diagnosis for that case, with its own
        // cleanup (`git reset`, leaving the working tree exactly as the operator left it) that the
        // stderr check below does not perform. This is not a benign-warning allow-list: both branches
        // still refuse to start; this only decides which of the two refusals fires, and by a
        // structural test (would committing now record a gitlink), not by pattern-matching stderr text.
        //
        // A tree with both faults at once (a gitlink *and* an unreadable directory) surfaces only the
        // gitlink refusal here — nothing is discarded (git reset below unstages everything cleanly, the
        // working tree is exactly as the operator left it), but the unreadable directory is not named
        // in this refusal's message. The operator sees it only on the *next* restart, after fixing the
        // gitlink. Diagnosis is serial rather than lost, but it does cost a second restart.
        var gitlinkPaths = await FindStagedGitlinksAsync(repositoryRoot, cancellationToken);
        if (gitlinkPaths.Count > 0)
        {
            // Unstage before throwing, so a refused start is not also a half-staged one — `git reset`
            // resets the index back to HEAD without touching the working tree, leaving exactly what
            // the operator copied in untouched for them to fix.
            await _git.RunOrThrowAsync(repositoryRoot, ["reset"], cancellationToken: cancellationToken);

            throw new InvalidOperationException(BuildGitlinkErrorMessage(repositoryRoot, gitlinkPaths));
        }

        if (!string.IsNullOrWhiteSpace(addResult.StandardError))
        {
            throw UnexpectedReconciliationStderrException(repositoryRoot, addResult.StandardError);
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
    /// unconditionally before reconciliation in <see cref="AcceptRepositoryAsync"/> and either creates
    /// the initial commit or refuses to start when history exists with no working tree — so the
    /// genuinely unborn-<c>HEAD</c> case (a fresh repository with no commits at all) never actually
    /// reaches this method or the <c>git reset</c> below it. (As a property of git, <c>diff --cached
    /// --raw</c> *would* diff against the empty tree if it ever did face an unborn <c>HEAD</c> — but
    /// that is not why this code is safe today, and would only become load-bearing if a future change
    /// reordered <c>AcceptRepositoryAsync</c> to run reconciliation before the initial commit.)
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
