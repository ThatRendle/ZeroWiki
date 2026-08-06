using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZeroWiki.Identity;

namespace ZeroWiki.Content;

/// <summary>
/// Commit-on-save (D4, D9, D16, D17, §6 block C2): writes a browser save to the working tree and
/// creates exactly one git commit per save-point, authored as the logged-in ZeroWiki user, under D16's
/// single cross-process write lock, gated by D4's base-revision compare-and-swap.
/// </summary>
/// <remarks>
/// <b>Order of operations, and the order is the design (D17)</b> — numbered because <see cref="SaveAsync"/>'s
/// own body is commented against these same numbers:
/// <list type="number">
/// <item>Resolve the route to a working-tree path and reject a non-canonical request (S2).</item>
/// <item>Acquire the write lock, bounded by <see cref="ContentStorageOptions.SaveWriteLockTimeout"/> —
/// the only step in this method the caller's <see cref="CancellationToken"/> still governs (§6 block D2,
/// Product Owner decision): once acquired, every step below runs to completion regardless of the
/// request's own cancellation, because a killed <c>git commit</c> can strand <c>.git/index.lock</c> and
/// nothing in this codebase clears one. <see cref="SaveAsync"/>'s own body names the seam explicitly as
/// <c>lockHeldCancellation</c>.</item>
/// <item>Re-read the base revision under the lock — D4's CAS is only a CAS because the re-read happens
/// after acquiring exclusive access, not before.</item>
/// <item>Compare against the declared base.</item>
/// <item>Refuse to follow a symlink sitting at the resolved path, then write.</item>
/// <item>Stage the one path (never <c>-A</c>); nothing staged means success with no commit — byte-
/// identical content is not an error.</item>
/// <item>Commit, authored via <see cref="AccountGitAuthorFactory"/>.</item>
/// <item>On any failure past the write — the commit exiting non-zero, or an exception from <c>add</c>/
/// <c>diff</c>/<c>commit</c> — restore the working tree and invalidate the page index through one shared
/// path, <see cref="HandlePostWriteFailureAsync"/>, which guarantees invalidation runs even when the
/// restore itself fails (a reviewer finding on this block's first pass): a failed restore is precisely
/// when stale index metadata is most dangerous, since the abandoned save's bytes may still be on disk
/// and the index may already be serving metadata read from them. Restore still precedes invalidation on
/// every path that restores successfully — the reverse order there would leave a window where a refresh
/// reads still-dirty bytes and installs with a generation that legitimately matches — but that ordering
/// is no longer allowed to gate whether invalidation happens at all.</item>
/// </list>
/// </remarks>
public sealed class PageSaveService
{
    private readonly ContentPaths _paths;
    private readonly GitProcessRunner _git;
    private readonly AccountGitAuthorFactory _authorFactory;
    private readonly IPageIndex _index;
    private readonly PageHistoryService _historyService;
    private readonly ILogger<PageSaveService> _logger;
    private readonly TimeSpan _saveWriteLockTimeout;

    public PageSaveService(
        ContentPaths paths,
        GitProcessRunner git,
        AccountGitAuthorFactory authorFactory,
        IPageIndex index,
        PageHistoryService historyService,
        ILogger<PageSaveService> logger,
        IOptions<ContentStorageOptions> options)
    {
        _paths = paths;
        _git = git;
        _authorFactory = authorFactory;
        _index = index;
        _historyService = historyService;
        _logger = logger;
        _saveWriteLockTimeout = options.Value.SaveWriteLockTimeout;
    }

    /// <summary>
    /// Writes <paramref name="content"/> to the page <paramref name="routeValue"/> resolves to and
    /// commits it as <paramref name="account"/>, honouring <paramref name="baseRevision"/>'s
    /// optimistic-concurrency check (D4). See this class's own remarks for the numbered order of
    /// operations this method's body is commented against.
    /// </summary>
    public async Task<SavePageResult> SaveAsync(
        RouteValue routeValue,
        string content,
        PageBaseRevision baseRevision,
        AuthenticatedAccount account,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(account);

        // 1. Resolve, then reject anything that is not the canonical request for the file it resolved
        // to (S2, D17) -- Encode is not injective, so a decode-then-re-encode round trip alone can land
        // on the same file two different request strings would never both legitimately have been
        // served under, which would otherwise re-open D12's collision through the save door.
        if (!PageRouteCodec.TryResolveWorkingTreePathFromRouteValue(_paths, routeValue, out var absolutePath))
        {
            return SavePageResult.Refused;
        }

        var relativePath = Path.GetRelativePath(_paths.WorkingTree, absolutePath);
        var canonicalRoute = PageRouteCodec.Encode(relativePath);
        if (!PageRouteCodec.IsCanonicalRouteValue(routeValue, canonicalRoute))
        {
            return SavePageResult.Refused;
        }

        var repositoryRelativePath = _historyService.RepositoryRelativeWorkingTree + "/" +
            relativePath.Replace(Path.DirectorySeparatorChar, '/');

        // 2. Acquire the write lock, bounded by the save-side (not the startup) timeout. Expiry here is
        // a per-request, recoverable failure -- distinct from D4's conflict, and nothing has been
        // written yet, so there is nothing to roll back.
        RepositoryWriteLock writeLock;
        try
        {
            writeLock = await RepositoryWriteLock.AcquireAsync(_paths.LockFilePath, _saveWriteLockTimeout, cancellationToken);
        }
        catch (RepositoryLockTimeoutException ex)
        {
            _logger.LogWarning(
                "Save for route '{RouteValue}' waited {Timeout} for the repository write lock and gave " +
                "up; nothing was written. The repository is busy (another save or an in-progress push).",
                routeValue,
                ex.Timeout);
            return SavePageResult.RepositoryBusy;
        }

        try
        {
            // From here until the lock is released (the `finally` below), the request's own
            // cancellation no longer applies -- Product Owner decision, §6 block D2. A `git commit`
            // killed mid-flight can strand `.git/index.lock`, and nothing in this codebase clears
            // one: measured by execution, a stranded lock fails `git add -A`/`git commit` with exit
            // 128 while `git status --porcelain` and `git rev-parse HEAD` still read a clean tree at
            // exit 0 -- it would fail D9's own startup reconciliation while reporting the tree
            // healthy. `RepositoryWriteLock.AcquireAsync` above is the only cancellable step in this
            // method (D17's "repository busy" path); everything below deliberately uses this token,
            // never the caller's `cancellationToken`, so a reader can see exactly where cancellation
            // stops applying rather than infer it from an argument that happens to be omitted.
            var lockHeldCancellation = CancellationToken.None;

            // 3. Re-read the base revision under the lock -- this is what makes the compare in step 4 a
            // CAS rather than a check: read-then-write is not atomic across processes, and a base
            // revision read before the lock may already be stale by the time it is acted on.
            var probe = await ProbeHeadPathAsync(repositoryRelativePath, lockHeldCancellation);
            if (probe.State == HeadPathState.Refuse)
            {
                return SavePageResult.Refused;
            }

            // 4. Compare. Equal-including-both-null covers "declared absent, still absent" (a brand new
            // page nobody else created meanwhile) and "declared a blob, that exact blob is still at
            // HEAD" identically; any other combination is a lost-update hazard (D4).
            if (!string.Equals(baseRevision.BlobSha, probe.BlobSha, StringComparison.Ordinal))
            {
                return SavePageResult.Conflict;
            }

            // Git models a symlink as a blob, so the probe above cannot distinguish one (D17) -- a
            // symlink arriving by push can sit at a save's resolved path and read as an ordinary
            // present blob. Checked here, under the lock, immediately before the write it guards: no
            // concurrent writer can plant one between this check and the write below, because every
            // repository write (a push included, D3) serializes through the same lock this call is
            // already holding.
            if (AnyPathComponentIsASymlink(_paths.WorkingTree, absolutePath))
            {
                _logger.LogWarning(
                    "Refusing to save '{RepositoryRelativePath}': a symlink occupies the resolved path " +
                    "or one of its ancestor directories. Nothing was written.",
                    repositoryRelativePath);
                return SavePageResult.Refused;
            }

            // 5. Write.
            await WriteFileAsync(absolutePath, content, lockHeldCancellation);

            // 6-7. Stage, short-circuit on nothing-staged, commit. Any exception this block throws
            // (`add` failing, or anything else -- the caller's own CancellationToken cannot fire here,
            // it stopped applying above) is handled by the single catch below exactly like an ordinary
            // non-zero commit exit -- one rollback path, not two independently-written ones that can
            // silently disagree on what "handled" means.
            GitProcessResult commitResult;
            try
            {
                // git add <the one path> -- scoped, never -A: this class only ever writes the one path
                // it just resolved, and staging anything else would be staging content this save never
                // touched.
                await _git.RunOrThrowAsync(
                    _paths.RepositoryRoot,
                    ["add", "--", repositoryRelativePath],
                    cancellationToken: lockHeldCancellation);

                // Nothing staged -> success, no commit. Byte-identical content is not an error and must
                // not fall into the rollback path; `git commit` would otherwise fail "nothing to commit"
                // and report a failure to a member who did nothing wrong.
                var stagedDiff = await _git.RunAsync(
                    _paths.RepositoryRoot,
                    ["diff", "--cached", "--quiet", "--", repositoryRelativePath],
                    cancellationToken: lockHeldCancellation);
                if (stagedDiff.Succeeded)
                {
                    return SavePageResult.Saved(commitSha: null);
                }

                var author = _authorFactory.CreateAuthor(account);
                commitResult = await _git.RunAsync(
                    _paths.RepositoryRoot,
                    ["commit", "-m", $"Save '{canonicalRoute}'"],
                    environmentVariables: author.ToEnvironmentVariables(),
                    cancellationToken: lockHeldCancellation);
            }
            catch (Exception ex)
            {
                return await HandlePostWriteFailureAsync(
                    repositoryRelativePath, absolutePath, probe.State, triggeringException: ex, failedCommitResult: null);
            }

            if (commitResult.Succeeded)
            {
                var headSha = await _git.RunOrThrowAsync(
                    _paths.RepositoryRoot,
                    ["rev-parse", "HEAD"],
                    cancellationToken: lockHeldCancellation);
                return SavePageResult.Saved(headSha.StandardOutput.Trim());
            }

            return await HandlePostWriteFailureAsync(
                repositoryRelativePath, absolutePath, probe.State, triggeringException: null, failedCommitResult: commitResult);
        }
        finally
        {
            writeLock.Dispose();
        }
    }

    /// <summary>
    /// Writes <paramref name="content"/> via a temp-file-then-atomic-rename (<see cref="File.Move(string, string, bool)"/>
    /// performs a same-volume POSIX <c>rename(2)</c>), so a fault partway through writing never leaves
    /// <paramref name="absolutePath"/> holding neither its old content nor its new content — a plain
    /// truncate-and-overwrite would risk exactly that if it failed mid-write. The temp file's name never
    /// ends in <c>.md</c>, so a concurrent read (enumeration, or a page render — neither is gated by the
    /// write lock, D15) can never mistake it for a page while it briefly exists on disk.
    /// </summary>
    private static async Task WriteFileAsync(string absolutePath, string content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);

        var tempPath = $"{absolutePath}.zerowiki-tmp-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllTextAsync(tempPath, content, cancellationToken);
            File.Move(tempPath, absolutePath, overwrite: true);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            throw;
        }
    }

    /// <summary>
    /// The single path every failure past the write funnels through — an exception from <c>add</c>/
    /// <c>diff</c>/<c>commit</c> (<paramref name="triggeringException"/> set), or <c>commit</c> itself
    /// exiting non-zero (<paramref name="failedCommitResult"/> set). Exactly one of the two is set;
    /// never both, never neither.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Reviewer finding, first pass on this block:</b> the previous shape called the rollback from
    /// <em>two</em> places — the commit-failure branch, and a <c>catch</c> built for exceptions — and the
    /// commit-failure branch's own call sat <em>inside</em> the block the <c>catch</c> wrapped. If the
    /// rollback itself threw (<c>git reset</c>/<c>checkout</c> failing, or <see cref="File.Delete(string)"/>
    /// throwing), the <c>catch</c> would run the rollback a <em>second</em> time, and if that also threw,
    /// <see cref="PageIndex.Invalidate"/> would never run at all on either attempt — the tree left dirty
    /// with the write lock already released and no later trigger to notice, since a rollback never
    /// advances <c>HEAD</c> for anything to detect. This method exists so the rollback runs exactly once
    /// per <see cref="SaveAsync"/> call, from exactly one call site.
    /// </para>
    /// <para>
    /// <b>Invalidation is unconditional on whether the restore below succeeds</b> (the substance of the
    /// same finding) — a failed restore is precisely when stale index metadata is most dangerous: the
    /// abandoned save's bytes may still be on disk, and the index may already be serving metadata read
    /// from them, so waiting for a successful restore before disowning that metadata gets the risk
    /// backwards. The <c>finally</c> below is what makes this true regardless of whether the <c>try</c>
    /// returns normally or throws — restore-then-invalidate ordering still holds on the path that
    /// restores successfully (D17's window argument), it is just no longer a precondition for
    /// invalidation to happen at all.
    /// </para>
    /// <para>
    /// A restore that itself fails is reported as <see cref="SaveOutcome.RollbackFailed"/>, distinct
    /// from <see cref="SaveOutcome.Failed"/> (an ordinary commit failure that rolled back cleanly): the
    /// working tree may still be dirty outside the write lock's hold, which D9's startup reconciliation
    /// will recover on the next restart, but is not something this per-request result can silently fold
    /// into "the save failed, try again" without losing that a repository-level fault occurred.
    /// </para>
    /// </remarks>
    private async Task<SavePageResult> HandlePostWriteFailureAsync(
        string repositoryRelativePath,
        string absolutePath,
        HeadPathState baseState,
        Exception? triggeringException,
        GitProcessResult? failedCommitResult)
    {
        Exception? rollbackFailure = null;
        try
        {
            await RollBackFailedSaveAsync(repositoryRelativePath, absolutePath, baseState, CancellationToken.None);
        }
        catch (Exception ex)
        {
            rollbackFailure = ex;
        }
        finally
        {
            _index.Invalidate();
        }

        if (rollbackFailure is not null)
        {
            var combined = triggeringException is not null
                ? new AggregateException(triggeringException, rollbackFailure)
                : rollbackFailure;

            _logger.LogCritical(
                combined,
                "Save for '{RepositoryRelativePath}' failed, AND the rollback meant to restore the " +
                "working tree also failed. The working tree may now be dirty outside a lock-held save " +
                "-- the page index was invalidated regardless, but the repository itself needs operator " +
                "attention; D9's startup reconciliation will recover a dirty tree on the next restart.",
                repositoryRelativePath);

            return SavePageResult.RollbackFailed;
        }

        if (triggeringException is not null)
        {
            _logger.LogError(
                triggeringException,
                "Save for '{RepositoryRelativePath}' failed before it could commit; the working tree " +
                "was restored and the page index invalidated.",
                repositoryRelativePath);
        }
        else
        {
            _logger.LogError(
                "Commit failed for '{RepositoryRelativePath}' (exit {ExitCode}: {StandardError}); the " +
                "working tree was restored and the page index invalidated.",
                repositoryRelativePath,
                failedCommitResult!.ExitCode,
                failedCommitResult.StandardError.Trim());
        }

        return SavePageResult.Failed;
    }

    /// <summary>
    /// Unstages this save's dirty write, then restores the working tree to match what git now believes
    /// about <paramref name="repositoryRelativePath"/> — its committed content if <paramref name="baseState"/>
    /// was <see cref="HeadPathState.Blob"/> (the page already existed at <c>HEAD</c>), or simply deleted
    /// if it was <see cref="HeadPathState.Absent"/> (this save's own write is the only thing that ever
    /// created the path, so there is nothing for git to restore it to).
    /// </summary>
    private async Task RollBackFailedSaveAsync(
        string repositoryRelativePath,
        string absolutePath,
        HeadPathState baseState,
        CancellationToken cancellationToken)
    {
        // Unstage first, regardless of which case: the index currently holds this save's dirty content.
        // A bare, path-scoped `git reset` restores the index entry to HEAD's own state -- present if
        // the page already existed there, removed entirely if it did not -- the same "unstage before
        // deciding what else to do" shape ContentRepositoryService.ReconcileWorkingTreeAsync's own
        // gitlink rollback already uses.
        await _git.RunOrThrowAsync(
            _paths.RepositoryRoot,
            ["reset", "--", repositoryRelativePath],
            cancellationToken: cancellationToken);

        if (baseState == HeadPathState.Blob)
        {
            // The index now matches HEAD after the reset above, so checking out from the index
            // reproduces HEAD's own committed content for this path.
            await _git.RunOrThrowAsync(
                _paths.RepositoryRoot,
                ["checkout", "--", repositoryRelativePath],
                cancellationToken: cancellationToken);
        }
        else if (File.Exists(absolutePath))
        {
            // Nothing for git to restore this path to -- it was never committed. `git checkout --` on a
            // path git no longer tracks after the reset above would refuse with "pathspec ... did not
            // match any file(s) known to git", so the rollback is a plain delete instead.
            File.Delete(absolutePath);
        }
    }

    /// <summary>
    /// D17: <c>git ls-tree HEAD --format='%(objecttype) %(objectname)' -- &lt;repo-relative path&gt;</c>,
    /// which discriminates every state this method needs — present, absent, or a fault — without
    /// consulting an exit code for any of them beyond "did this call succeed at all". Never trusts
    /// <c>rev-parse HEAD:&lt;path&gt;</c>'s doubled exit-128 meaning (D17's own reason for not using it).
    /// </summary>
    private async Task<HeadPathProbe> ProbeHeadPathAsync(string repositoryRelativePath, CancellationToken cancellationToken)
    {
        var arguments = new[]
        {
            "ls-tree", "HEAD", "--format=%(objecttype) %(objectname)", "--", repositoryRelativePath,
        };

        var result = await _git.RunAsync(_paths.RepositoryRoot, arguments, cancellationToken: cancellationToken);
        if (!result.Succeeded)
        {
            // Non-zero exit: HEAD unborn/invalid (128) or any other resolution fault. D17's posture:
            // an exit code is trusted only for what git documents it to mean, and no documented meaning
            // here distinguishes one fault from another usefully enough to act on -- refuse rather than
            // guess.
            return new HeadPathProbe(HeadPathState.Refuse, null);
        }

        var output = result.StandardOutput.TrimEnd('\n', '\r');
        if (output.Length == 0)
        {
            return new HeadPathProbe(HeadPathState.Absent, null);
        }

        // A single "blob <sha>" line is the only shape this class's own call site can ever produce --
        // PageRouteCodec.TryDecodeCore refuses empty segments and always appends ".md", so
        // repositoryRelativePath can never carry a trailing separator, the input D17 names as the one
        // that makes ls-tree recurse and print one line per child. Guarded anyway, at no real cost: a
        // lone newline-free "blob <sha>" line is required, not merely a "blob " prefix, so a future
        // caller that loses that guarantee gets Refuse instead of an arbitrary child's object name.
        if (!output.Contains('\n', StringComparison.Ordinal) && output.StartsWith("blob ", StringComparison.Ordinal))
        {
            return new HeadPathProbe(HeadPathState.Blob, output["blob ".Length..]);
        }

        // "tree <sha>" (the route resolves to a directory), "commit <sha>" (a gitlink), or anything
        // else -- every one of these is a fault this method does not know how to act on (D17's
        // catch-all; a gitlink is genuinely reachable here even though §2's C# scan never lets one enter
        // via this app's own writes, because it can arrive by push).
        return new HeadPathProbe(HeadPathState.Refuse, null);
    }

    /// <summary>
    /// Whether any already-existing path component from <see cref="ContentPaths.WorkingTree"/> down to
    /// <paramref name="absolutePath"/> (the leaf itself included, if it exists) is a symlink.
    /// </summary>
    /// <remarks>
    /// <see cref="PageRouteCodec.TryResolveWorkingTreePathFromRouteValue"/>'s containment check is
    /// lexical (<see cref="Path.GetFullPath(string)"/>, which does not resolve symlinks) — it cannot by
    /// itself catch a symlinked ancestor directory smuggling the write outside <c>docs/</c>, only reject
    /// a route whose <em>string</em> does not lexically stay under the working tree. This check catches
    /// both the leaf case D17 names explicitly (a symlink arriving by push sitting exactly at a save's
    /// resolved path, reading as an ordinary present blob to <see cref="ProbeHeadPathAsync"/>) and a
    /// symlinked ancestor directory, using the same <see cref="FileAttributes.ReparsePoint"/> check
    /// already established twice elsewhere in this namespace
    /// (<see cref="PageEnumerationService"/>'s walk, <see cref="ContentRepositoryService"/>'s
    /// nested-repository scan) rather than a third, differently-shaped one.
    /// </remarks>
    private static bool AnyPathComponentIsASymlink(string workingTree, string absolutePath)
    {
        var current = workingTree;
        var relativeToWorkingTree = Path.GetRelativePath(workingTree, absolutePath);

        foreach (var segment in relativeToWorkingTree.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);

            if (!File.Exists(current) && !Directory.Exists(current))
            {
                // Nothing here yet, and therefore nothing deeper either -- a save creating a new page
                // (or new intermediate directories for one) has nothing further to check.
                return false;
            }

            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private enum HeadPathState { Absent, Blob, Refuse }

    private readonly record struct HeadPathProbe(HeadPathState State, string? BlobSha);
}
